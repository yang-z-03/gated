using System;
using System.Collections.Generic;
using System.Linq;

namespace gated.Network;

public sealed class FastLocalMovingAlgorithm : StandardLocalMovingAlgorithm
{
    public FastLocalMovingAlgorithm() { }
    public FastLocalMovingAlgorithm(Random random) : base(random) { }
    public FastLocalMovingAlgorithm(double resolution, int n_iterations, Random random) : base(resolution, random) { }
}




