using System;
using System.Collections.Generic;
using System.Linq;

namespace gated.Network;

public class VOSLayoutAlgorithm : QualityLayoutAlgorithm
{
    public const int DefaultAttraction = 2;
    public const int DefaultRepulsion = 1;
    public const double DefaultEdgeWeightIncrement = 1.0;
    protected internal int attraction;
    protected internal int repulsion;
    protected internal double edge_weight_increment;

    public VOSLayoutAlgorithm() : this(DefaultAttraction, DefaultRepulsion, DefaultEdgeWeightIncrement) { }
    public VOSLayoutAlgorithm(int attraction, int repulsion, double edge_weight_increment) { this.attraction = attraction; this.repulsion = repulsion; this.edge_weight_increment = edge_weight_increment; }
    public VOSLayoutAlgorithm Clone() => new(attraction, repulsion, edge_weight_increment);
    public int GetAttraction() => attraction;
    public int GetRepulsion() => repulsion;
    public double GetEdgeWeightIncrement() => edge_weight_increment;
    public void SetAttraction(int attraction) => this.attraction = attraction;
    public void SetRepulsion(int repulsion) => this.repulsion = repulsion;
    public void SetEdgeWeightIncrement(double edge_weight_increment) => this.edge_weight_increment = edge_weight_increment;

    public double CalcQuality(Network network, Layout layout)
    {
        var quality = 0.0;
        for (var i = 0; i < network.n_nodes; i++)
            for (var e = network.first_neighbor_indices[i]; e < network.first_neighbor_indices[i + 1]; e++)
            {
                var j = network.neighbor_array.Get(e);
                if (i < j)
                {
                    var distance = Distance(layout, i, j);
                    quality += (network.edge_weight_array.Get(e) + edge_weight_increment) * Math.Pow(distance, attraction) / attraction;
                }
            }
        for (var i = 0; i < network.n_nodes; i++)
            for (var j = i + 1; j < network.n_nodes; j++)
            {
                var distance = Math.Max(Distance(layout, i, j), 1e-12);
                quality -= repulsion == 0 ? Math.Log(distance) : Math.Pow(distance, -repulsion) / repulsion;
            }
        return quality;
    }

    protected static double Distance(Layout layout, int a, int b)
    {
        var dx = layout.coordinates[a][0] - layout.coordinates[b][0];
        var dy = layout.coordinates[a][1] - layout.coordinates[b][1];
        return Math.Sqrt(dx * dx + dy * dy);
    }
}




