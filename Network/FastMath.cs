using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace gated.Network;

public static class FastMath
{
    public static double FastExp(double exponent) => Math.Exp(exponent);
    public static double FastPow(double @base, int exponent) => Math.Pow(@base, exponent);
}

