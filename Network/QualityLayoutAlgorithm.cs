using System;
using System.Collections.Generic;
using System.Linq;

namespace gated.Network;

public interface QualityLayoutAlgorithm
{
    double CalcQuality(Network network, Layout layout);
}




