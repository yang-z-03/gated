using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using gated.Models;
using gated.Reduction;

namespace gated.Services;

public static class PlatformPresentationBuilder
{
    private const int maximum_integration_preview_events_per_population = 20_000;
    private const int maximum_univariate_preview_events_per_population = 5_000;
    private const int plot_distribution_bins = 500;
    private static readonly ConditionalWeakTable<IntegrationPlatform, IntegrationPresentationCache> integration_caches = new();
    private static readonly ConditionalWeakTable<Platform, UnivariatePresentationSampleCache> univariate_sample_caches = new();

    public static PlatformPresentation Integration(IntegrationPlatform platform)
    {
        if (!platform.HasIntegrated || platform.Normalized is not { } matrix || platform.RowMap.Count == 0)
            return PlatformPresentation.Empty;
        return integration_caches.GetValue(platform, _ => new IntegrationPresentationCache())
            .Get(platform, matrix);
    }

    public static PlatformPresentation Univariate(
        UnivariatePlatform platform,
        string plot_key,
        string plot_title,
        string default_table_key,
        bool default_plot,
        bool include_fit,
        bool include_components,
        bool include_observed = true)
    {
        var observed = (!include_observed ||
                        platform.Transformed is null ||
                        platform.PlotSeries.Any(series => series.Role == PlatformSeriesRole.Observed)
                ? Array.Empty<PlatformPlotSeries>()
                : distributions(platform, platform.Transformed, 0, normalized_input: true))
            .Where(item => source_visible(platform, item.SourceId))
            .ToArray();
        var scripted = platform.PlotSeries
            .Where(item => source_visible(platform, item.SourceId))
            .Where(item => item.Role != PlatformSeriesRole.Fit || include_fit)
            .Where(item => item.Role != PlatformSeriesRole.Component || include_components)
            .Select(item => raw_x_series(platform, item))
            .ToArray();
        var parameterized = platform.FitCurves
            .Where(item => source_visible(platform, item.SourceId))
            .Where(item => item.Role != PlatformSeriesRole.Fit || include_fit)
            .Where(item => item.Role != PlatformSeriesRole.Component || include_components)
            .Select(item => parameterized_series(platform, item))
            .ToArray();
        var plot = plot_document(platform, plot_key, plot_title, platform.SelectedFeatureNames.FirstOrDefault() ?? "Intensity", observed.Concat(parameterized).Concat(scripted).ToArray());
        return presentation(platform, plot, default_table_key, default_plot);
    }

    private static PlatformPresentation presentation(Platform platform, PlatformPlotDocument plot, string default_table_key, bool default_plot = true)
    {
        var tables = platform.ResultTables.Select(table => new PlatformTableDocument
        {
            Key = table.Key,
            Title = table.Title,
            Columns = table.Columns,
            Rows = table.Rows.ToArray()
        }).ToArray();
        var outputs = new List<PlatformLayoutOutput>();
        if (plot.Series.Count > 0)
            outputs.Add(new PlatformLayoutOutput(plot.Key, plot.Title, PlatformLayoutOutputKind.Plot, default_plot));
        foreach (var table in tables)
            outputs.Add(new PlatformLayoutOutput(table.Key, table.Title, PlatformLayoutOutputKind.Table,
                string.Equals(table.Key, default_table_key, StringComparison.Ordinal)));
        return new PlatformPresentation { Plots = plot.Series.Count > 0 ? [plot] : [], Tables = tables, Outputs = outputs };
    }

    private static PlatformPlotDocument plot_document(Platform platform, string key, string title, string x_label, IReadOnlyList<PlatformPlotSeries> series) =>
        new()
        {
            Key = key,
            Title = title,
            XLabel = string.IsNullOrWhiteSpace(x_label) ? "Intensity" : x_label,
            YLabel = "Normalized frequency",
            XTransform = platform.Axis.Transform,
            XScale = platform_axis_scale(platform),
            Minimum = platform.Axis.Minimum,
            Maximum = platform.Axis.Maximum,
            Logicle = platform.Axis.Logicle,
            Series = series
        };

    private static PlatformPresentation build_integration_presentation(IntegrationPlatform platform, float[,] matrix)
    {
        var plots = new List<PlatformPlotDocument>();
        var sampled_rows = integration_sample_rows(platform, matrix.GetLength(0));
        var channels = platform.SelectedFeatureNames;
        for (int column = 0; column < channels.Length && column < matrix.GetLength(1); column++)
        {
            string channel = channels[column];
            var scale = integration_axis_scale(platform, channel);
            var values_by_source = new double[sampled_rows.Length][];
            for (int source_id = 0; source_id < sampled_rows.Length; source_id++)
            {
                values_by_source[source_id] = sampled_rows[source_id]
                    .Select(row => scale.InverseTransform(matrix[row, column]))
                    .Where(double.IsFinite)
                    .ToArray();
            }

            var range = automatic_integration_range(platform, channel, scale, values_by_source);
            var series = new List<PlatformPlotSeries>();
            for (int source_id = 0; source_id < values_by_source.Length; source_id++)
            {
                if (!source_visible(platform, source_id) || values_by_source[source_id].Length == 0)
                    continue;
                series.Add(integration_histogram(
                    platform,
                    channel,
                    source_id,
                    values_by_source[source_id],
                    scale,
                    range.Minimum,
                    range.Maximum));
            }

            if (series.Count == 0)
                continue;
            string title = channel;
            plots.Add(new PlatformPlotDocument
            {
                Key = integration_plot_key(channel),
                Title = title,
                XLabel = channel,
                YLabel = "Normalized frequency",
                XTransform = platform_transform_kind(scale.Kind),
                XScale = scale,
                Minimum = range.Minimum,
                Maximum = range.Maximum,
                Logicle = scale.Logicle,
                Series = series
            });
        }

        var outputs = plots
            .Select(plot => new PlatformLayoutOutput(plot.Key, plot.Title, PlatformLayoutOutputKind.Plot, false))
            .ToArray();
        return new PlatformPresentation { Plots = plots, Outputs = outputs };
    }

    private static int[][] integration_sample_rows(IntegrationPlatform platform, int matrix_row_count)
    {
        var result = new int[platform.RowMap.Sources.Count][];
        var reservoirs = Enumerable.Range(0, result.Length)
            .Select(_ => new List<int>(Math.Min(
                maximum_integration_preview_events_per_population,
                Math.Max(16, matrix_row_count / Math.Max(1, result.Length)))))
            .ToArray();
        var seen = new int[result.Length];
        var random = Enumerable.Range(0, result.Length)
            .Select(source_id => new Random(HashCode.Combine(platform.Id, source_id, matrix_row_count)))
            .ToArray();
        int row_count = Math.Min(matrix_row_count, platform.RowMap.SourceIds.Length);
        for (int row = 0; row < row_count; row++)
        {
            int source_id = platform.RowMap.SourceIds[row];
            if (source_id < 0 || source_id >= reservoirs.Length)
                continue;
            int count = ++seen[source_id];
            var reservoir = reservoirs[source_id];
            if (reservoir.Count < maximum_integration_preview_events_per_population)
                reservoir.Add(row);
            else
            {
                int replacement = random[source_id].Next(count);
                if (replacement < maximum_integration_preview_events_per_population)
                    reservoir[replacement] = row;
            }
        }
        for (int source_id = 0; source_id < result.Length; source_id++)
            result[source_id] = reservoirs[source_id].ToArray();
        return result;
    }

    private static (double Minimum, double Maximum) automatic_integration_range(
        IntegrationPlatform platform,
        string channel,
        AxisScale scale,
        IReadOnlyList<double[]> values_by_source)
    {
        var values = values_by_source.SelectMany(item => item).Where(double.IsFinite).ToArray();
        var options = platform.Transformations.TryGetValue(channel, out var configured) ? configured : null;
        double fallback_minimum = options?.Minimum ?? platform.Axis.Minimum;
        double fallback_maximum = options?.Maximum ?? platform.Axis.Maximum;
        if (values.Length == 0)
            return valid_range(fallback_minimum, fallback_maximum);

        double observed_minimum = values.Min();
        double observed_maximum = values.Max();
        if (scale.Kind == CoordinateScaleKind.Linear)
            return valid_range(Math.Min(-0.1, observed_minimum), Math.Max(1.1, observed_maximum));

        double transformed_minimum = scale.Transform(observed_minimum);
        double transformed_maximum = scale.Transform(observed_maximum);
        double span = transformed_maximum - transformed_minimum;
        if (!double.IsFinite(span) || span <= 0)
            return valid_range(fallback_minimum, fallback_maximum);
        return valid_range(
            scale.InverseTransform(transformed_minimum - 0.1 * span),
            scale.InverseTransform(transformed_maximum + 0.1 * span));
    }

    private static (double Minimum, double Maximum) valid_range(double minimum, double maximum)
    {
        if (!double.IsFinite(minimum)) minimum = 0;
        if (!double.IsFinite(maximum)) maximum = minimum + 1;
        if (maximum <= minimum) maximum = minimum + 1;
        return (minimum, maximum);
    }

    private static PlatformPlotSeries integration_histogram(
        IntegrationPlatform platform,
        string channel,
        int source_id,
        IReadOnlyList<double> values,
        AxisScale scale,
        double minimum,
        double maximum)
    {
        const int bins = plot_distribution_bins;
        double transformed_minimum = scale.Transform(minimum);
        double transformed_maximum = scale.Transform(maximum);
        double width = (transformed_maximum - transformed_minimum) / bins;
        var x = new double[bins];
        var y = new double[bins];
        for (int bin = 0; bin < bins; bin++)
            x[bin] = scale.InverseTransform(transformed_minimum + (bin + 0.5) * width);
        foreach (double raw in values)
        {
            int bin = (int)Math.Floor((scale.Transform(raw) - transformed_minimum) / width);
            if (bin >= 0 && bin < bins)
                y[bin]++;
        }
        double total = y.Sum();
        if (total > 0)
            for (int index = 0; index < y.Length; index++)
                y[index] /= total;
        return new PlatformPlotSeries
        {
            Key = $"observed:{channel}:{source_id}",
            Title = source_label(platform, source_id),
            XLabel = channel,
            YLabel = "Normalized frequency",
            SourceId = source_id,
            Role = PlatformSeriesRole.Observed,
            X = x,
            Y = y
        };
    }

    private static PlatformPlotSeries[] distributions(Platform platform, float[,] matrix, int column, bool normalized_input)
    {
        if (column < 0 || column >= matrix.GetLength(1) || platform.RowMap.Count == 0)
            return [];
        var cache = univariate_sample_caches.GetValue(platform, _ => new UnivariatePresentationSampleCache());
        return cache.GetDistributions(platform, matrix, column, normalized_input, sampled_rows =>
        {
            var result = new List<PlatformPlotSeries>();
            for (int source_id = 0; source_id < platform.RowMap.Sources.Count; source_id++)
            {
                var values = new List<double>();
                foreach (int row in sampled_rows[source_id])
                {
                    double value = matrix[row, column];
                    if (normalized_input)
                        value = inverse_transform(platform, value);
                    if (double.IsFinite(value))
                        values.Add(value);
                }
                if (values.Count == 0)
                    continue;
                result.Add(histogram(platform, source_id, values));
            }
            return result.ToArray();
        });
    }

    private static PlatformPlotSeries histogram(Platform platform, int source_id, IReadOnlyList<double> values)
    {
        const int bins = plot_distribution_bins;
        double transformed_minimum = transform(platform, platform.Axis.Minimum);
        double transformed_maximum = transform(platform, platform.Axis.Maximum);
        if (!double.IsFinite(transformed_minimum) || !double.IsFinite(transformed_maximum) || transformed_maximum <= transformed_minimum)
        {
            transformed_minimum = values.Select(value => transform(platform, value)).Where(double.IsFinite).DefaultIfEmpty(0).Min();
            transformed_maximum = values.Select(value => transform(platform, value)).Where(double.IsFinite).DefaultIfEmpty(1).Max();
        }
        if (transformed_maximum <= transformed_minimum)
            transformed_maximum = transformed_minimum + 1;
        var x = new double[bins];
        var y = new double[bins];
        double width = (transformed_maximum - transformed_minimum) / bins;
        for (int bin = 0; bin < bins; bin++)
            x[bin] = inverse_transform(platform, transformed_minimum + (bin + 0.5) * width);
        foreach (double raw in values)
        {
            double value = transform(platform, raw);
            int bin = (int)Math.Floor((value - transformed_minimum) / width);
            if (bin >= 0 && bin < bins)
                y[bin]++;
        }
        double total = y.Sum();
        if (total > 0)
            for (int index = 0; index < y.Length; index++)
                y[index] /= total;
        if (platform is UnivariatePlatform { EnableSmoothing: true } univariate)
            y = smooth(y, univariate.SmoothingWindow);
        return new PlatformPlotSeries
        {
            Key = $"observed:{source_id}",
            Title = source_label(platform, source_id),
            XLabel = platform.SelectedFeatureNames.FirstOrDefault() ?? "Intensity",
            YLabel = "Normalized frequency",
            SourceId = source_id,
            Role = PlatformSeriesRole.Observed,
            X = x,
            Y = y
        };
    }

    private static PlatformPlotSeries raw_x_series(Platform platform, PlatformPlotSeries series) => new()
    {
        Key = series.Key,
        Title = series.Title,
        XLabel = series.XLabel,
        YLabel = series.YLabel,
        SourceId = series.SourceId,
        Role = series.Role,
        X = series.X.Select(value => inverse_transform(platform, value)).ToArray(),
        Y = series.Y
    };

    private static PlatformPlotSeries parameterized_series(Platform platform, PlatformFitCurve curve)
    {
        const int points = plot_distribution_bins;
        double transformed_minimum = transform(platform, platform.Axis.Minimum);
        double transformed_maximum = transform(platform, platform.Axis.Maximum);
        if (!double.IsFinite(transformed_minimum) || !double.IsFinite(transformed_maximum) || transformed_maximum <= transformed_minimum)
        {
            transformed_minimum = 0;
            transformed_maximum = 1;
        }

        var x = new double[points];
        var y = new double[points];
        for (int index = 0; index < points; index++)
        {
            double transformed = transformed_minimum + index * (transformed_maximum - transformed_minimum) / (points - 1);
            x[index] = inverse_transform(platform, transformed);
            double value = evaluate_fit_curve(platform, curve, transformed, new HashSet<string>(StringComparer.Ordinal));
            y[index] = double.IsFinite(value) ? value / Math.Max(curve.Normalizer, double.Epsilon) : double.NaN;
        }

        return new PlatformPlotSeries
        {
            Key = curve.Key,
            Title = curve.Title,
            XLabel = string.IsNullOrWhiteSpace(curve.XLabel) ? platform.SelectedFeatureNames.FirstOrDefault() ?? "Intensity" : curve.XLabel,
            YLabel = string.IsNullOrWhiteSpace(curve.YLabel) ? "Normalized frequency" : curve.YLabel,
            SourceId = curve.SourceId,
            Role = curve.Role,
            X = x,
            Y = y
        };
    }

    private static double evaluate_fit_curve(Platform platform, PlatformFitCurve curve, double x, ISet<string> path)
    {
        var parameters = curve.Parameters;
        return curve.Kind switch
        {
            PlatformFitCurveKind.Gaussian when parameters.Length >= 3 =>
                gaussian(x, parameters[0], parameters[1], parameters[2]),
            PlatformFitCurveKind.GaussianSum => gaussian_sum(x, parameters),
            PlatformFitCurveKind.CellCycleSum => cell_cycle_sum(x, parameters),
            PlatformFitCurveKind.Linear when parameters.Length >= 2 => parameters[0] * x + parameters[1],
            PlatformFitCurveKind.Exponential when parameters.Length >= 3 =>
                parameters[2] + Math.Exp(Math.Clamp(parameters[0] * x + parameters[1], -700, 700)),
            PlatformFitCurveKind.Gamma when parameters.Length >= 3 =>
                parameters[2] * gamma_density(x, parameters[0], parameters[1]),
            PlatformFitCurveKind.Addition => addition(platform, curve, x, path),
            _ => double.NaN
        };
    }

    private static double gaussian_sum(double x, IReadOnlyList<double> parameters)
    {
        double result = 0;
        for (int index = 0; index + 2 < parameters.Count; index += 3)
            result += gaussian(x, parameters[index], parameters[index + 1], parameters[index + 2]);
        return result;
    }

    private static double cell_cycle_sum(double x, IReadOnlyList<double> parameters)
    {
        if (parameters.Count < 9)
            return double.NaN;
        double result = gaussian(x, parameters[0], parameters[1], parameters[2]) +
                        gaussian(x, parameters[3], parameters[4], parameters[5]);
        double minimum = parameters[6];
        double maximum = parameters[7];
        if (x < minimum || x > maximum || maximum <= minimum)
            return result;
        double t = (x - minimum) / (maximum - minimum);
        double polynomial = 0;
        for (int index = parameters.Count - 1; index >= 8; index--)
            polynomial = polynomial * t + parameters[index];
        return result + Math.Max(0, polynomial);
    }

    private static double gaussian(double x, double amplitude, double mean, double sigma)
    {
        sigma = Math.Max(Math.Abs(sigma), 1e-12);
        double z = (x - mean) / sigma;
        return amplitude * Math.Exp(-0.5 * z * z);
    }

    private static double gamma_density(double x, double shape, double scale)
    {
        if (x <= 0 || shape <= 0 || scale <= 0)
            return 0;
        double log = (shape - 1) * Math.Log(x) - x / scale - log_gamma(shape) - shape * Math.Log(scale);
        return Math.Exp(log);
    }

    private static double log_gamma(double value)
    {
        double[] coefficients = [676.5203681218851, -1259.1392167224028, 771.32342877765313,
            -176.61502916214059, 12.507343278686905, -0.13857109526572012,
            9.9843695780195716e-6, 1.5056327351493116e-7];
        if (value < 0.5)
            return Math.Log(Math.PI) - Math.Log(Math.Sin(Math.PI * value)) - log_gamma(1 - value);
        value -= 1;
        double x = 0.99999999999980993;
        for (int index = 0; index < coefficients.Length; index++)
            x += coefficients[index] / (value + index + 1);
        double t = value + coefficients.Length - 0.5;
        return 0.5 * Math.Log(2 * Math.PI) + (value + 0.5) * Math.Log(t) - t + Math.Log(x);
    }

    private static double addition(Platform platform, PlatformFitCurve curve, double x, ISet<string> path)
    {
        if (!path.Add(curve.Key))
            return double.NaN;
        double result = curve.Intercept;
        for (int index = 0; index < curve.ModelKeys.Length && index < curve.Weights.Length; index++)
        {
            var model = platform.FitCurves.FirstOrDefault(item => item.Key == curve.ModelKeys[index]);
            if (model is null)
                continue;
            result += curve.Weights[index] * evaluate_fit_curve(platform, model, x, path);
        }
        path.Remove(curve.Key);
        return result;
    }

    private static bool source_visible(Platform platform, int source_id)
    {
        if (source_id < 0)
            return true;
        if (source_id >= platform.RowMap.Sources.Count)
            return false;
        var source = platform.RowMap.Sources[source_id];
        return platform.Populations.Any(row =>
            row.GroupId == source.GroupId && row.SampleId == source.SampleId && row.GateId == source.GateId &&
            row.Region == source.Region && row.IsSelected);
    }

    private static string source_label(Platform platform, int source_id)
    {
        if (source_id < 0 || source_id >= platform.RowMap.Sources.Count)
            return "Population";
        var source = platform.RowMap.Sources[source_id];
        var row = platform.Populations.FirstOrDefault(item =>
            item.GroupId == source.GroupId && item.SampleId == source.SampleId && item.GateId == source.GateId && item.Region == source.Region);
        return row?.DisplayName ?? $"Population {source_id + 1}";
    }

    private static string integration_plot_key(string channel) =>
        $"integration:{Uri.EscapeDataString(channel)}";

    private static AxisScale integration_axis_scale(IntegrationPlatform platform, string channel)
    {
        if (platform.Transformations.TryGetValue(channel, out var options))
        {
            return new AxisScale
            {
                Kind = coordinate_scale_kind(options.Kind),
                Logicle = options.Logicle,
                ArcsinhCofactor = options.ArcsinhCofactor
            };
        }

        var kind = Configuration.DefaultCoordinateScaleForChannel(channel);
        return new AxisScale
        {
            Kind = kind,
            Logicle = platform.Axis.Logicle,
            ArcsinhCofactor = 5.0
        };
    }

    private static AxisScale platform_axis_scale(Platform platform) => new()
    {
        Kind = coordinate_scale_kind(platform.Axis.Transform),
        Logicle = platform.Axis.Logicle,
        ArcsinhCofactor = arcsinh_cofactor(platform)
    };

    private static CoordinateScaleKind coordinate_scale_kind(PlatformTransformationKind kind) => kind switch
    {
        PlatformTransformationKind.Logarithm => CoordinateScaleKind.Logarithmic,
        PlatformTransformationKind.Logicle => CoordinateScaleKind.Logicle,
        PlatformTransformationKind.Arcsinh => CoordinateScaleKind.Arcsinh,
        _ => CoordinateScaleKind.Linear
    };

    private static PlatformTransformationKind platform_transform_kind(CoordinateScaleKind kind) => kind switch
    {
        CoordinateScaleKind.Logarithmic => PlatformTransformationKind.Logarithm,
        CoordinateScaleKind.Logicle => PlatformTransformationKind.Logicle,
        CoordinateScaleKind.Arcsinh => PlatformTransformationKind.Arcsinh,
        _ => PlatformTransformationKind.Linear
    };

    private sealed class IntegrationPresentationCache
    {
        private float[,]? matrix;
        private int[]? source_ids;
        private string signature = "";
        private PlatformPresentation presentation = PlatformPresentation.Empty;

        public PlatformPresentation Get(IntegrationPlatform platform, float[,] current_matrix)
        {
            string current_signature = string.Join("|",
                string.Join(",", platform.SelectedFeatureNames),
                string.Join(";", platform.SelectedFeatureNames.Select(channel =>
                    platform.Transformations.TryGetValue(channel, out var options)
                        ? $"{channel}:{options.Kind}:{options.Minimum:G17}:{options.Maximum:G17}:{options.Logicle.T:G17}:{options.Logicle.W:G17}:{options.Logicle.M:G17}:{options.Logicle.A:G17}:{options.ArcsinhCofactor:G17}"
                        : channel)),
                string.Join(",", platform.Populations.Select(row => $"{row.RowKey}:{row.IsSelected}")));
            if (ReferenceEquals(matrix, current_matrix) &&
                ReferenceEquals(source_ids, platform.RowMap.SourceIds) &&
                string.Equals(signature, current_signature, StringComparison.Ordinal))
                return presentation;

            matrix = current_matrix;
            source_ids = platform.RowMap.SourceIds;
            signature = current_signature;
            presentation = build_integration_presentation(platform, current_matrix);
            return presentation;
        }
    }

    private sealed class UnivariatePresentationSampleCache
    {
        private int matrix_row_count = -1;
        private string source_signature = "";
        private int[][] sampled_rows = [];
        private int[] first_rows = [];
        private int[] last_rows = [];
        private float[,]? distribution_matrix;
        private int distribution_column = -1;
        private bool distribution_normalized;
        private string distribution_signature = "";
        private PlatformPlotSeries[] cached_distributions = [];

        public PlatformPlotSeries[] GetDistributions(
            Platform platform,
            float[,] current_matrix,
            int column,
            bool normalized,
            Func<int[][], PlatformPlotSeries[]> create)
        {
            var samples = Get(platform, current_matrix.GetLength(0));
            string current_signature = string.Join("|",
                platform.Axis.Transform,
                platform.Axis.Minimum.ToString("G17"),
                platform.Axis.Maximum.ToString("G17"),
                platform.Axis.Logicle.T.ToString("G17"),
                platform.Axis.Logicle.W.ToString("G17"),
                platform.Axis.Logicle.M.ToString("G17"),
                platform.Axis.Logicle.A.ToString("G17"),
                arcsinh_cofactor(platform).ToString("G17"),
                platform is UnivariatePlatform univariate ? univariate.EnableSmoothing : false,
                platform is UnivariatePlatform smoothing ? smoothing.SmoothingWindow : 0);
            if (ReferenceEquals(distribution_matrix, current_matrix) &&
                distribution_column == column &&
                distribution_normalized == normalized &&
                string.Equals(distribution_signature, current_signature, StringComparison.Ordinal))
                return cached_distributions;

            distribution_matrix = current_matrix;
            distribution_column = column;
            distribution_normalized = normalized;
            distribution_signature = current_signature;
            cached_distributions = create(samples);
            return cached_distributions;
        }

        public int[][] Get(Platform platform, int current_matrix_row_count)
        {
            string current_signature = string.Join(";", platform.RowMap.Sources.Select(source =>
                $"{source.GroupId}:{source.SampleId}:{source.GateId}:{source.Region}"));
            if (matrix_row_count == current_matrix_row_count &&
                sampled_rows.Length == platform.RowMap.Sources.Count &&
                string.Equals(source_signature, current_signature, StringComparison.Ordinal) &&
                boundaries_match(platform.RowMap.SourceIds))
                return sampled_rows;

            matrix_row_count = current_matrix_row_count;
            source_signature = current_signature;
            build_samples(platform);
            distribution_matrix = null;
            return sampled_rows;
        }

        private bool boundaries_match(IReadOnlyList<int> source_ids)
        {
            if (source_ids.Count < matrix_row_count || first_rows.Length != sampled_rows.Length)
                return false;
            for (int source_id = 0; source_id < first_rows.Length; source_id++)
            {
                int first = first_rows[source_id];
                int last = last_rows[source_id];
                if (first < 0 != (last < 0))
                    return false;
                if (first >= 0 && (first >= source_ids.Count || last >= source_ids.Count ||
                                   source_ids[first] != source_id || source_ids[last] != source_id))
                    return false;
                if (first > 0 && source_ids[first - 1] == source_id)
                    return false;
                if (last >= 0 && last + 1 < matrix_row_count && source_ids[last + 1] == source_id)
                    return false;
            }
            return true;
        }

        private void build_samples(Platform platform)
        {
            int source_count = platform.RowMap.Sources.Count;
            sampled_rows = new int[source_count][];
            first_rows = Enumerable.Repeat(-1, source_count).ToArray();
            last_rows = Enumerable.Repeat(-1, source_count).ToArray();
            var reservoirs = Enumerable.Range(0, source_count)
                .Select(_ => new List<int>(Math.Min(
                    maximum_univariate_preview_events_per_population,
                    Math.Max(16, matrix_row_count / Math.Max(1, source_count)))))
                .ToArray();
            var seen = new int[source_count];
            var random = Enumerable.Range(0, source_count)
                .Select(source_id => new Random(HashCode.Combine(platform.Id, source_id, matrix_row_count)))
                .ToArray();
            int row_count = Math.Min(matrix_row_count, platform.RowMap.SourceIds.Length);
            for (int row = 0; row < row_count; row++)
            {
                int source_id = platform.RowMap.SourceIds[row];
                if (source_id < 0 || source_id >= source_count)
                    continue;
                if (first_rows[source_id] < 0)
                    first_rows[source_id] = row;
                last_rows[source_id] = row;
                int count = ++seen[source_id];
                var reservoir = reservoirs[source_id];
                if (reservoir.Count < maximum_univariate_preview_events_per_population)
                    reservoir.Add(row);
                else
                {
                    int replacement = random[source_id].Next(count);
                    if (replacement < maximum_univariate_preview_events_per_population)
                        reservoir[replacement] = row;
                }
            }
            for (int source_id = 0; source_id < source_count; source_id++)
                sampled_rows[source_id] = reservoirs[source_id].ToArray();
        }
    }

    private static double transform(Platform platform, double value) => platform.Axis.Transform switch
    {
        PlatformTransformationKind.Logicle => new LogicleTransform(platform.Axis.Logicle).Transform(value),
        PlatformTransformationKind.Logarithm => Math.Sign(value) * Math.Log10(1 + Math.Abs(value)),
        PlatformTransformationKind.Arcsinh => Math.Asinh(value / arcsinh_cofactor(platform)),
        _ => value
    };

    private static double inverse_transform(Platform platform, double value) => platform.Axis.Transform switch
    {
        PlatformTransformationKind.Logicle => new LogicleTransform(platform.Axis.Logicle).InverseTransform(value),
        PlatformTransformationKind.Logarithm => Math.Sign(value) * (Math.Pow(10, Math.Abs(value)) - 1),
        PlatformTransformationKind.Arcsinh => Math.Sinh(value) * arcsinh_cofactor(platform),
        _ => value
    };

    private static double arcsinh_cofactor(Platform platform) =>
        platform is UnivariatePlatform univariate ? univariate.ArcsinhCofactor : 5.0;

    private static double[] smooth(double[] values, int half_window)
    {
        if (half_window <= 0 || values.Length == 0)
            return values;
        var result = new double[values.Length];
        for (int index = 0; index < values.Length; index++)
        {
            int start = Math.Max(0, index - half_window);
            int end = Math.Min(values.Length - 1, index + half_window);
            double sum = 0;
            for (int current = start; current <= end; current++)
                sum += values[current];
            result[index] = sum / (end - start + 1);
        }
        return result;
    }
}
