using System;
using System.Collections.Generic;
using System.Linq;

namespace gated.Network;

public interface QualityClusteringAlgorithm
{
    double CalcQuality(Network network, Clustering clustering);
}




