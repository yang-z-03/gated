using System;
using System.Collections.Generic;
using System.Linq;

namespace gated.Network;

public interface ClusteringAlgorithm
{
    Clustering FindClustering(Network network);
}




