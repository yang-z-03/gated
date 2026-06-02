using System;
using System.Collections.Generic;
using System.Linq;

namespace gated.Network;

public sealed class Layout
{
    internal int n_nodes;
    internal double[][] coordinates;

    public Layout(int n_nodes) : this(n_nodes, new Random()) { }
    public Layout(int n_nodes, Random random)
    {
        this.n_nodes = n_nodes;
        coordinates = new double[n_nodes][];
        InitRandomCoordinates(random);
    }

    public Layout(double[][] coordinates)
    {
        n_nodes = coordinates.Length;
        this.coordinates = coordinates.Select(static c => (double[])c.Clone()).ToArray();
    }

    public Layout Clone() => new(coordinates);
    public int GetNNodes() => n_nodes;
    public double[][] GetCoordinates() => coordinates.Select(static c => (double[])c.Clone()).ToArray();
    public double[] GetCoordinates(int node) => (double[])coordinates[node].Clone();
    public void SetCoordinates(int node, double[] coordinates) => this.coordinates[node] = (double[])coordinates.Clone();
    public double[] GetMinCoordinates() => [coordinates.Min(static c => c[0]), coordinates.Min(static c => c[1])];
    public double[] GetMaxCoordinates() => [coordinates.Max(static c => c[0]), coordinates.Max(static c => c[1])];

    public double GetAverageDistance()
    {
        var sum = 0.0;
        var count = 0;
        for (var i = 0; i < n_nodes; i++)
            for (var j = i + 1; j < n_nodes; j++)
            {
                sum += Distance(i, j);
                count++;
            }
        return count == 0 ? 0 : sum / count;
    }

    public void InitRandomCoordinates() => InitRandomCoordinates(new Random());
    public void InitRandomCoordinates(Random random)
    {
        for (var i = 0; i < n_nodes; i++)
            coordinates[i] = [random.NextDouble() - 0.5, random.NextDouble() - 0.5];
    }

    public void Standardize(bool standardizeDistances)
    {
        var center = new[] { coordinates.Average(static c => c[0]), coordinates.Average(static c => c[1]) };
        for (var i = 0; i < n_nodes; i++)
        {
            coordinates[i][0] -= center[0];
            coordinates[i][1] -= center[1];
        }
        if (standardizeDistances)
        {
            var avg = GetAverageDistance();
            if (avg > 0)
                for (var i = 0; i < n_nodes; i++)
                {
                    coordinates[i][0] /= avg;
                    coordinates[i][1] /= avg;
                }
        }
    }

    public void Rotate(double angle)
    {
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        for (var i = 0; i < n_nodes; i++)
        {
            var x = coordinates[i][0];
            var y = coordinates[i][1];
            coordinates[i][0] = cos * x - sin * y;
            coordinates[i][1] = sin * x + cos * y;
        }
    }

    public void Flip(int dimension)
    {
        for (var i = 0; i < n_nodes; i++)
            coordinates[i][dimension] = -coordinates[i][dimension];
    }

    private double Distance(int a, int b)
    {
        var dx = coordinates[a][0] - coordinates[b][0];
        var dy = coordinates[a][1] - coordinates[b][1];
        return Math.Sqrt(dx * dx + dy * dy);
    }
}




