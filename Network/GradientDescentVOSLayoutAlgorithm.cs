using System;
using System.Collections.Generic;
using System.Linq;

namespace gated.Network;

public sealed class GradientDescentVOSLayoutAlgorithm : VOSLayoutAlgorithm, LayoutAlgorithm
{
    internal Random random;
    internal int max_n_iterations = 1000;
    internal double initial_step_size = 1.0;
    internal double min_step_size = 0.001;
    internal double step_size_reduction = 0.75;
    internal int required_n_quality_value_improvements = 5;

    public GradientDescentVOSLayoutAlgorithm() : this(new Random()) { }
    public GradientDescentVOSLayoutAlgorithm(Random random) => this.random = random;
    public GradientDescentVOSLayoutAlgorithm(int attraction, int repulsion, double edge_weight_increment, Random random) : base(attraction, repulsion, edge_weight_increment) => this.random = random;
    public int GetMaxNIterations() => max_n_iterations;
    public double GetInitialStepSize() => initial_step_size;
    public double GetMinStepSize() => min_step_size;
    public double GetStepSizeReduction() => step_size_reduction;
    public int GetRequiredNQualityValueImprovements() => required_n_quality_value_improvements;
    public void SetMaxNIterations(int max_n_iterations) => this.max_n_iterations = max_n_iterations;
    public void SetInitialStepSize(double initial_step_size) => this.initial_step_size = initial_step_size;
    public void SetMinStepSize(double min_step_size) => this.min_step_size = min_step_size;
    public void SetStepSizeReduction(double step_size_reduction) => this.step_size_reduction = step_size_reduction;
    public void SetRequiredNQualityValueImprovements(int required_n_quality_value_improvements) => this.required_n_quality_value_improvements = required_n_quality_value_improvements;

    public Layout FindLayout(Network network)
    {
        var layout = new Layout(network.n_nodes, random);
        ImproveLayout(network, layout);
        return layout;
    }

    public void ImproveLayout(Network network, Layout layout)
    {
        var step = initial_step_size;
        var best = CalcQuality(network, layout);
        var improvements = 0;
        for (var iteration = 0; iteration < max_n_iterations && step >= min_step_size; iteration++)
        {
            var node = random.Next(network.n_nodes);
            var old = layout.GetCoordinates(node);
            var candidate = new[] { old[0] + (random.NextDouble() - 0.5) * step, old[1] + (random.NextDouble() - 0.5) * step };
            layout.SetCoordinates(node, candidate);
            var quality = CalcQuality(network, layout);
            if (quality < best)
            {
                best = quality;
                improvements++;
            }
            else
            {
                layout.SetCoordinates(node, old);
                if (improvements >= required_n_quality_value_improvements)
                {
                    improvements = 0;
                    step *= step_size_reduction;
                }
            }
        }
        layout.Standardize(true);
    }
}




