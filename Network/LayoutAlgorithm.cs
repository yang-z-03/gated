using System;
using System.Collections.Generic;
using System.Linq;

namespace gated.Network;

public interface LayoutAlgorithm
{
    Layout FindLayout(Network network);
}




