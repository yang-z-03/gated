using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace gated.Reduction
{
    public class Umap<T> where T : IUmapDataPoint
    {
        private const float smooth_k_tolerance = 1e-5f;
        private const float min_k_dist_scale = 1e-3f;

        private readonly float learning_rate = 1f;
        private readonly float local_connectivity = 1f;
        private readonly float min_dist = 0.1f;
        private readonly int negative_sample_rate = 5;
        private readonly float repulsion_strength = 1;
        private readonly float set_op_mix_ratio = 1;
        private readonly float spread = 1;

        private readonly DistanceCalculation<T> distance_fn;
        private readonly IProvideRandomValues random_provider;
        private readonly int neighbor_count;
        private readonly int? custom_number_of_epochs;
        private readonly ProgressReporter? progress_reporter;

        // KNN state (can be precomputed and supplied via initializeFit)
        private int[][]? knn_indices;
        private float[][]? knn_distances;

        // Internal graph connectivity representation
        private SparseMatrix? fuzzy_graph;
        private T[]? source_data;
        private bool is_initialized;
        private Tree<T>.FlatTree[] rp_forest = Array.Empty<Tree<T>.FlatTree>();

        // Projected embedding
        private float[] embedding = Array.Empty<float>();
        private readonly OptimizationState optimization_state;

        /// <summary>
        /// The progress will be a value from 0 to 1 that indicates approximately how much of the processing has been completed
        /// </summary>
        public delegate void ProgressReporter(float progress);

        public Umap(
            DistanceCalculation<T>? distance = null,
            IProvideRandomValues? random = null,
            int dimensions = 2,
            int numberOfNeighbors = 15,
            int? customNumberOfEpochs = null,
            ProgressReporter? progressReporter = null)
        {
            if ((customNumberOfEpochs != null) && (customNumberOfEpochs <= 0))
            {
                throw new ArgumentOutOfRangeException(nameof(customNumberOfEpochs), "if non-null then must be a positive value");
            }

            distance_fn = distance ?? DistanceFunctions.Cosine;
            random_provider = random ?? DefaultRandomGenerator.Instance;
            neighbor_count = numberOfNeighbors;
            optimization_state = new OptimizationState { Dim = dimensions };
            custom_number_of_epochs = customNumberOfEpochs;
            progress_reporter = progressReporter;
        }

        /// <summary>
        /// Initializes fit by computing KNN and a fuzzy simplicial set, as well as initializing the projected embeddings. Sets the optimization state ahead of optimization steps.
        /// Returns the number of epochs to be used for the SGD optimization.
        /// </summary>
        public int InitializeFit(T[] x)
        {
            // We don't need to reinitialize if we've already initialized for this data
            if ((source_data == x) && is_initialized)
            {
                return GetNEpochs();
            }

            // For large quantities of data (which is where the progress estimating is more useful), InitializeFit takes at least 80% of the total time (the calls to Step are
            // completed much more quickly AND they naturally lend themselves to granular progress updates; one per loop compared to the recommended number of epochs)
            var initializeFitProgressReporter = ScaleProgressReporter(progress_reporter, 0, 0.8f);

            source_data = x;
            if ((knn_indices is null) && (knn_distances is null))
            {
                // This part of the process very roughly accounts for 1/3 of the work
                (knn_indices, knn_distances) = NearestNeighbors(x, ScaleProgressReporter(initializeFitProgressReporter, 0, 0.3f));
            }

            // This part of the process very roughly accounts for 2/3 of the work (the reamining work is in the Step calls)
            fuzzy_graph = FuzzySimplicialSet(x, neighbor_count, set_op_mix_ratio, ScaleProgressReporter(initializeFitProgressReporter, 0.3f, 1));

            var (head, tail, epochsPerSample) = InitializeSimplicialSetEmbedding();

            // Set the optimization routine state
            optimization_state.Head = head;
            optimization_state.Tail = tail;
            optimization_state.EpochsPerSample = epochsPerSample;

            // Now, initialize the optimization steps
            InitializeOptimization();
            PrepareForOptimizationLoop();
            is_initialized = true;

            return GetNEpochs();
        }

        public int InitializeFit(T[] x, int[][] knnIndices, float[][] knnDistances)
        {
            if (knnIndices.Length != x.Length || knnDistances.Length != x.Length)
            {
                throw new ArgumentException("KNN arrays must have one row per data point.");
            }

            knn_indices = knnIndices.Select(static row => (int[])row.Clone()).ToArray();
            knn_distances = knnDistances.Select(static row => (float[])row.Clone()).ToArray();
            source_data = null;
            is_initialized = false;
            return InitializeFit(x);
        }

        public float[][] GetEmbedding()
        {
            var final = new float[optimization_state.NumberOfVertices][];
            Span<float> span = embedding.AsSpan();
            for (int i = 0; i < optimization_state.NumberOfVertices; i++)
            {
                final[i] = span.Slice(i * optimization_state.Dim, optimization_state.Dim).ToArray();
            }
            return final;
        }

        /// <summary>
        /// Gets the number of epochs for optimizing the projection - NOTE: This heuristic differs from the python version
        /// </summary>
        private int GetNEpochs()
        {
            if (custom_number_of_epochs != null)
            {
                return custom_number_of_epochs.Value;
            }

            if (fuzzy_graph is null)
            {
                throw new InvalidOperationException("UMAP graph has not been initialized.");
            }

            var length = fuzzy_graph.Dims.rows;
            if (length <= 2500)
            {
                return 500;
            }
            else if (length <= 5000)
            {
                return 400;
            }
            else if (length <= 7500)
            {
                return 300;
            }
            else
            {
                return 200;
            }
        }

        /// <summary>
        /// Compute the ``nNeighbors`` nearest points for each data point in ``X`` - this may be exact, but more likely is approximated via nearest neighbor descent.
        /// </summary>
        internal (int[][] knnIndices, float[][] knnDistances) NearestNeighbors(T[] x, ProgressReporter progressReporter)
        {
            var metricNNDescent = NNDescent<T>.MakeNNDescent(distance_fn, random_provider);
            progressReporter(0.05f);
            var nTrees = 5 + Round(MathF.Sqrt(x.Length) / 20f);
            var nIters = Math.Max(5, (int)Math.Floor(Math.Round(Math.Log2(x.Length))));
            progressReporter(0.1f);
            var leafSize = Math.Max(10, neighbor_count);
            var forestProgressReporter = ScaleProgressReporter(progressReporter, 0.1f, 0.4f);
            rp_forest = Enumerable.Range(0, nTrees)
                .Select(i =>
                {
                    forestProgressReporter((float)i / nTrees);
                    return Tree<T>.FlattenTree(Tree<T>.MakeTree(x, leafSize, i, random_provider), leafSize);
                })
                .ToArray();
            var leafArray = Tree<T>.MakeLeafArray(rp_forest);
            progressReporter(0.45f);
            var nnDescendProgressReporter = ScaleProgressReporter(progressReporter, 0.5f, 1);

            return metricNNDescent(x, leafArray, neighbor_count, nIters, startingIteration: (i, max) => nnDescendProgressReporter((float)i / max));

            // Handle python3 rounding down from 0.5 discrpancy
            int Round(double n) => (n == 0.5) ? 0 : (int)Math.Floor(Math.Round(n));
        }

        /// <summary>
        /// Given a set of data X, a neighborhood size, and a measure of distance compute the fuzzy simplicial set(here represented as a fuzzy graph in the form of a sparse matrix) associated
        /// to the data. This is done by locally approximating geodesic distance at each point, creating a fuzzy simplicial set for each such point, and then combining all the local fuzzy
        /// simplicial sets into a global one via a fuzzy union.
        /// </summary>
        private SparseMatrix FuzzySimplicialSet(T[] x, int nNeighbors, float setOpMixRatio, ProgressReporter progressReporter)
        {
            var knnIndices = knn_indices ?? new int[0][];
            var knnDistances = knn_distances ?? new float[0][];
            progressReporter(0.1f);
            var (sigmas, rhos) = SmoothKNNDistance(knnDistances, nNeighbors, local_connectivity);
            progressReporter(0.2f);
            var (rows, cols, vals) = ComputeMembershipStrengths(knnIndices, knnDistances, sigmas, rhos);
            progressReporter(0.3f);
            var sparseMatrix = new SparseMatrix(rows.AsSpan(), cols.AsSpan(), vals.AsSpan(), (x.Length, x.Length));
            var transpose = sparseMatrix.Transpose();
            var prodMatrix = sparseMatrix.PairwiseMultiply(transpose);
            progressReporter(0.4f);
            var a = sparseMatrix.Add(transpose).Subtract(prodMatrix);
            progressReporter(0.5f);
            var b = a.MultiplyScalar(setOpMixRatio);
            progressReporter(0.6f);
            var c = prodMatrix.MultiplyScalar(1 - setOpMixRatio);
            progressReporter(0.7f);
            var result = b.Add(c);
            progressReporter(0.8f);
            return result;
        }

        private static (float[] sigmas, float[] rhos) SmoothKNNDistance(float[][] distances, int k, float localConnectivity = 1, int nIter = 64, float bandwidth = 1)
        {
            var target = MathF.Log2(k) * bandwidth;
            var rho = new float[distances.Length];
            var result = new float[distances.Length];
            var rowMeans = new float[distances.Length];

            for (var i = 0; i < distances.Length; i++)
            {
                rowMeans[i] = Utils.Mean(distances[i]);
            }

            var globalMean = Utils.Mean(rowMeans);
            var globalMinScale = min_k_dist_scale * globalMean;

            for (var i = 0; i < distances.Length; i++)
            {
                var lo = 0f;
                var hi = float.MaxValue;
                var mid = 1f;

                var ithDistances = distances[i];
                Span<float> nonZeroBuffer = new float[ithDistances.Length];
                var nonZeroCount = 0;
                foreach (var distance in ithDistances)
                {
                    if (distance > 0)
                    {
                        nonZeroBuffer[nonZeroCount++] = distance;
                    }
                }

                var nonZeroDists = nonZeroBuffer[..nonZeroCount];
                if (nonZeroDists.Length >= localConnectivity)
                {
                    var index = (int)Math.Floor(localConnectivity);
                    var interpolation = localConnectivity - index;
                    if (index > 0)
                    {
                        rho[i] = nonZeroDists[index - 1];
                        if ((interpolation > smooth_k_tolerance) && (index < nonZeroDists.Length))
                        {
                            rho[i] += (float)(interpolation * (nonZeroDists[index] - nonZeroDists[index - 1]));
                        }
                    }
                    else if (nonZeroDists.Length > 0)
                    {
                        rho[i] = (float)(interpolation * nonZeroDists[0]);
                    }
                }
                else if (nonZeroDists.Length > 0)
                {
                    rho[i] = Utils.Max(nonZeroDists);
                }

                for (var n = 0; n < nIter; n++)
                {
                    var psum = 0f;
                    var row = ithDistances;
                    var rhoValue = rho[i];
                    for (var j = 1; j < row.Length; j++)
                    {
                        var d = row[j] - rhoValue;
                        if (d > 0)
                        {
                            psum += MathF.Exp(-(d / mid));
                        }
                        else
                        {
                            psum += 1.0f;
                        }
                    }
                    if (MathF.Abs(psum - target) < smooth_k_tolerance)
                    {
                        break;
                    }

                    if (psum > target)
                    {
                        hi = mid;
                        mid = (lo + hi) / 2;
                    }
                    else
                    {
                        lo = mid;
                        if (hi == float.MaxValue)
                        {
                            mid *= 2;
                        }
                        else
                        {
                            mid = (lo + hi) / 2;
                        }
                    }
                }

                result[i] = mid;

                var scaledRowMean = min_k_dist_scale * rowMeans[i];
                if (rho[i] > 0)
                {
                    if (result[i] < scaledRowMean)
                    {
                        result[i] = scaledRowMean;
                    }
                }
                else if (globalMinScale > 0)
                {
                    if (result[i] < globalMinScale)
                    {
                        result[i] = globalMinScale;
                    }
                }
            }
            return (result, rho);
        }

        private static (int[] rows, int[] cols, float[] vals) ComputeMembershipStrengths(int[][] knnIndices, float[][] knnDistances, float[] sigmas, float[] rhos)
        {
            var nSamples = knnIndices.Length;
            var nNeighbors = knnIndices[0].Length;

            var rows = new int[nSamples * nNeighbors];
            var cols = new int[nSamples * nNeighbors];
            var vals = new float[nSamples * nNeighbors];
            for (var i = 0; i < nSamples; i++)
            {
                for (var j = 0; j < nNeighbors; j++)
                {
                    if (knnIndices[i][j] == -1)
                    {
                        continue; // We didn't get the full knn for i
                    }

                    float val;
                    if (knnIndices[i][j] == i)
                    {
                        val = 0;
                    }
                    else if (knnDistances[i][j] - rhos[i] <= 0.0)
                    {
                        val = 1;
                    }
                    else
                    {
                        val = MathF.Exp(-((knnDistances[i][j] - rhos[i]) / sigmas[i]));
                    }

                    rows[i * nNeighbors + j] = i;
                    cols[i * nNeighbors + j] = knnIndices[i][j];
                    vals[i * nNeighbors + j] = val;
                }
            }
            return (rows, cols, vals);
        }

        /// <summary>
        /// Initialize a fuzzy simplicial set embedding, using a specified initialisation method and then minimizing the fuzzy set cross entropy between the 1-skeletons of the high and low
        /// dimensional fuzzy simplicial sets.
        /// </summary>
        private (int[] head, int[] tail, float[] epochsPerSample) InitializeSimplicialSetEmbedding()
        {
            if (fuzzy_graph is null)
            {
                throw new InvalidOperationException("UMAP graph has not been initialized.");
            }

            var n_epochs = GetNEpochs();
            var graphMax = 0f;
            foreach (var value in fuzzy_graph.GetValues())
            {
                if (graphMax < value)
                {
                    graphMax = value;
                }
            }

            var reduced_graph = fuzzy_graph.Map(value => (value < graphMax / n_epochs) ? 0 : value);

            // We're not computing the spectral initialization in this implementation until we determine a better eigenvalue/eigenvector computation approach

            embedding = new float[fuzzy_graph.Dims.rows * optimization_state.Dim];
            SimdRandom.Uniform(embedding, 10, random_provider);

            // Get graph data in ordered way...
            var weights = new List<float>();
            var head = new List<int>();
            var tail = new List<int>();
            foreach (var (row, col, value) in reduced_graph.GetAll())
            {
                if (value != 0)
                {
                    weights.Add(value);
                    tail.Add(row);
                    head.Add(col);
                }
            }
            ShuffleTogether(head, tail, weights);
            return (head.ToArray(), tail.ToArray(), MakeEpochsPerSample(weights.ToArray(), n_epochs));
        }

        private void ShuffleTogether<T1, T2, T3>(List<T1> list, List<T2> other, List<T3> weights)
        {
            int n = list.Count;
            if (other.Count != n) { throw new Exception(); }
            while (n > 1)
            {
                n--;
                int k = random_provider.Next(0, n + 1);
                T1 value = list[k];
                list[k] = list[n];
                list[n] = value;

                T2 otherValue = other[k];
                other[k] = other[n];
                other[n] = otherValue;

                T3 weightsValue = weights[k];
                weights[k] = weights[n];
                weights[n] = weightsValue;
            }
        }

        private static float[] MakeEpochsPerSample(float[] weights, int n_epochs)
        {
            var result = Utils.Filled(weights.Length, -1);
            var max = Utils.Max(weights);
            if (max <= 0)
            {
                return result;
            }

            for (var i = 0; i < weights.Length; i++)
            {
                var scaled = (weights[i] / max) * n_epochs;
                if (scaled > 0)
                {
                    result[i] = n_epochs / scaled;
                }
            }

            return result;
        }

        private void InitializeOptimization()
        {
            // Initialized in initializeSimplicialSetEmbedding()
            var head = optimization_state.Head;
            var tail = optimization_state.Tail;
            var epochsPerSample = optimization_state.EpochsPerSample;

            if (fuzzy_graph is null)
            {
                throw new InvalidOperationException("UMAP graph has not been initialized.");
            }

            var n_epochs = GetNEpochs();
            var n_vertices = fuzzy_graph.Dims.cols;

            var (a, b) = FindABParams(spread, min_dist);

            optimization_state.Head = head;
            optimization_state.Tail = tail;
            optimization_state.EpochsPerSample = epochsPerSample;
            optimization_state.A = a;
            optimization_state.B = b;
            optimization_state.NumberOfEpochs = n_epochs;
            optimization_state.NumberOfVertices = n_vertices;
        }

        internal static (float a, float b) FindABParams(float spread, float minDist)
        {
            // 2019-06-21 DWR: If we need to support other spread, minDist values then we might be able to use the LM implementation in Accord.NET but I'll hard code values that relate to the default configuration for now
            if ((spread != 1) || (minDist != 0.1f))
            {
                throw new ArgumentException($"Currently, the {nameof(FindABParams)} method only supports spread, minDist values of 1, 0.1 (the Levenberg-Marquardt algorithm is required to process other values");
            }

            return (1.5694704762346365f, 0.8941996053733949f);
        }

        private void PrepareForOptimizationLoop()
        {
            // Hyperparameters
            var repulsionStrength = repulsion_strength;
            var learningRate = learning_rate;
            var negativeSampleRate = negative_sample_rate;

            var epochsPerSample = optimization_state.EpochsPerSample;

            var dim = optimization_state.Dim;

            var epochsPerNegativeSample = new float[epochsPerSample.Length];
            var epochOfNextNegativeSample = new float[epochsPerSample.Length];
            var epochOfNextSample = new float[epochsPerSample.Length];

            for (var i = 0; i < epochsPerSample.Length; i++)
            {
                var epochValue = epochsPerSample[i];
                epochOfNextSample[i] = epochValue;
                var negativeValue = epochValue / negativeSampleRate;
                epochsPerNegativeSample[i] = negativeValue;
                epochOfNextNegativeSample[i] = negativeValue;
            }

            optimization_state.EpochOfNextSample = epochOfNextSample;
            optimization_state.EpochOfNextNegativeSample = epochOfNextNegativeSample;
            optimization_state.EpochsPerNegativeSample = epochsPerNegativeSample;

            optimization_state.MoveOther = true;
            optimization_state.InitialAlpha = learningRate;
            optimization_state.Alpha = learningRate;
            optimization_state.Gamma = repulsionStrength;
            optimization_state.Dim = dim;
        }

        /// <summary>
        /// Manually step through the optimization process one epoch at a time
        /// </summary>
        public int Step()
        {
            var currentEpoch = optimization_state.CurrentEpoch;
            var numberOfEpochsToComplete = GetNEpochs();
            if (currentEpoch < numberOfEpochsToComplete)
            {
                OptimizeLayoutStep(currentEpoch);
                if (progress_reporter is object)
                {
                    // InitializeFit roughly approximately takes 80% of the processing time for large quantities of data, leaving 20% for the Step iterations - the progress reporter
                    // calls made here are based on the assumption that Step will be called the recommended number of times (the number-of-epochs value returned from InitializeFit)
                    ScaleProgressReporter(progress_reporter, 0.8f, 1)((float)currentEpoch / numberOfEpochsToComplete);
                }
            }
            return optimization_state.CurrentEpoch;
        }

        /// <summary>
        /// Improve an embedding using stochastic gradient descent to minimize the fuzzy set cross entropy between the 1-skeletons of the high dimensional and low dimensional fuzzy simplicial sets.
        /// In practice this is done by sampling edges based on their membership strength(with the (1-p) terms coming from negative sampling similar to word2vec).
        /// </summary>
        private void OptimizeLayoutStep(int n)
        {
            if (random_provider.IsThreadSafe)
            {
                Parallel.For(0, optimization_state.EpochsPerSample.Length, Iterate);
            }
            else
            {
                for (var i = 0; i < optimization_state.EpochsPerSample.Length; i++)
                {
                    Iterate(i);
                }
            }

            optimization_state.Alpha = optimization_state.InitialAlpha * (1f - n / optimization_state.NumberOfEpochs);
            optimization_state.CurrentEpoch += 1;

            void Iterate(int i)
            {
                if (optimization_state.EpochOfNextSample[i] >= n)
                {
                    return;
                }

                Span<float> embeddingSpan = embedding.AsSpan();

                int j = optimization_state.Head[i];
                int k = optimization_state.Tail[i];

                var current = embeddingSpan.Slice(j * optimization_state.Dim, optimization_state.Dim);
                var other = embeddingSpan.Slice(k * optimization_state.Dim, optimization_state.Dim);

                var distSquared = RDist(current, other);
                var gradCoeff = 0f;

                if (distSquared > 0)
                {
                    gradCoeff = -2 * optimization_state.A * optimization_state.B * (float)Math.Pow(distSquared, optimization_state.B - 1);
                    gradCoeff /= optimization_state.A * (float)Math.Pow(distSquared, optimization_state.B) + 1;
                }

                const float clipValue = 4f;
                for (var d = 0; d < optimization_state.Dim; d++)
                {
                    var gradD = Clip(gradCoeff * (current[d] - other[d]), clipValue);
                    current[d] += gradD * optimization_state.Alpha;
                    if (optimization_state.MoveOther)
                    {
                        other[d] += -gradD * optimization_state.Alpha;
                    }
                }

                optimization_state.EpochOfNextSample[i] += optimization_state.EpochsPerSample[i];

                var nNegSamples = (int)Math.Floor((double)(n - optimization_state.EpochOfNextNegativeSample[i]) / optimization_state.EpochsPerNegativeSample[i]);

                for (var p = 0; p < nNegSamples; p++)
                {
                    k = random_provider.Next(0, optimization_state.NumberOfVertices);
                    other = embeddingSpan.Slice(k * optimization_state.Dim, optimization_state.Dim);
                    distSquared = RDist(current, other);
                    gradCoeff = 0f;
                    if (distSquared > 0)
                    {
                        gradCoeff = 2 * optimization_state.Gamma * optimization_state.B;
                        gradCoeff *= optimization_state.GetDistanceFactor(distSquared); //Preparation for future work for interpolating the table before optimizing
                    }
                    else if (j == k)
                    {
                        continue;
                    }

                    for (var d = 0; d < optimization_state.Dim; d++)
                    {
                        var gradD = 4f;
                        if (gradCoeff > 0)
                        {
                            gradD = Clip(gradCoeff * (current[d] - other[d]), clipValue);
                        }

                        current[d] += gradD * optimization_state.Alpha;
                    }
                }

                optimization_state.EpochOfNextNegativeSample[i] += nNegSamples * optimization_state.EpochsPerNegativeSample[i];
            }
        }

        /// <summary>
        /// Reduced Euclidean distance
        /// </summary>
        private static float RDist(Span<float> x, Span<float> y)
        {
            return Simd.Euclidean(x, y);
        }

        /// <summary>
        /// Standard clamping of a value into a fixed range
        /// </summary>
        private static float Clip(float x, float clipValue)
        {
            if (x > clipValue)
            {
                return clipValue;
            }
            else if (x < -clipValue)
            {
                return -clipValue;
            }
            else
            {
                return x;
            }
        }

        private static ProgressReporter ScaleProgressReporter(ProgressReporter? progressReporter, float start, float end)
        {
            if (progressReporter is null)
            {
                return _ => { };
            }

            var range = end - start;
            return progress => progressReporter((range * progress) + start);
        }

        public static class DistanceFunctions
        {
            public static float Cosine(T lhs, T rhs)
            {
                var lhsSpan = lhs.Data.AsSpan();
                var rhsSpan = rhs.Data.AsSpan();
                var denominator = Simd.Magnitude(lhsSpan) * Simd.Magnitude(rhsSpan);
                return 1 - (Simd.DotProduct(lhsSpan, rhsSpan) / denominator);
            }

            public static float CosineForNormalizedVectors(T lhs, T rhs)
            {
                var lhsSpan = lhs.Data.AsSpan();
                var rhsSpan = rhs.Data.AsSpan();
                return 1 - Simd.DotProduct(lhsSpan, rhsSpan);
            }

            public static float Euclidean(T lhs, T rhs)
            {
                var lhsSpan = lhs.Data.AsSpan();
                var rhsSpan = rhs.Data.AsSpan();
                return MathF.Sqrt(Simd.Euclidean(lhsSpan, rhsSpan));
            }
        }

        private sealed class OptimizationState
        {
            public int CurrentEpoch = 0;
            public int[] Head = new int[0];
            public int[] Tail = new int[0];
            public float[] EpochsPerSample = new float[0];
            public float[] EpochOfNextSample = new float[0];
            public float[] EpochOfNextNegativeSample = new float[0];
            public float[] EpochsPerNegativeSample = new float[0];
            public bool MoveOther = true;
            public float InitialAlpha = 1;
            public float Alpha = 1;
            public float Gamma = 1;
            public float A = 1.5769434603113077f;
            public float B = 0.8950608779109733f;
            public int Dim = 2;
            public int NumberOfEpochs = 500;
            public int NumberOfVertices = 0;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public float GetDistanceFactor(float distSquared) => 1f / ((0.001f + distSquared) * (float)(A * Math.Pow(distSquared, B) + 1));
        }
    }

    /// <inheritdoc cref="Umap{T}"/>
    public class Umap : Umap<RawVectorArrayUmapDataPoint>
    {
        /// <inheritdoc cref="Umap{T}"/>
        public Umap(
                DistanceCalculation<RawVectorArrayUmapDataPoint>? distance = null,
                IProvideRandomValues? random = null,
                int dimensions = 2,
                int numberOfNeighbors = 15,
                int? customNumberOfEpochs = null,
                ProgressReporter? progressReporter = null)
                    : base(distance, random, dimensions, numberOfNeighbors, customNumberOfEpochs, progressReporter)
        {

        }

        /// <inheritdoc cref="Umap{T}.NearestNeighbors(T[], Umap{T}.ProgressReporter)"/>
        public (int[][] knnIndices, float[][] knnDistances) NearestNeighbors(float[][] x, ProgressReporter progressReporter)
        {
            return base.NearestNeighbors(x.Select(c => new RawVectorArrayUmapDataPoint(c)).ToArray(), progressReporter);
        }

        /// <inheritdoc cref="Umap{T}.InitializeFit(T[])"/>
        public int InitializeFit(float[][] a) => base.InitializeFit(a.Select(x => new RawVectorArrayUmapDataPoint(x)).ToArray());

        public int InitializeFit(float[][] a, int[][] knnIndices, float[][] knnDistances) =>
            base.InitializeFit(a.Select(x => new RawVectorArrayUmapDataPoint(x)).ToArray(), knnIndices, knnDistances);

    }
}
