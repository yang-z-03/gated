using System;
using System.Collections.Generic;
using System.Linq;

namespace gated.Network;

public interface IncrementalClusteringAlgorithm : ClusteringAlgorithm
{
    bool ImproveClustering(Network network, Clustering clustering);
}




