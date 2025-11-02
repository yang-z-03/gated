using System;

namespace Gated.Models;

public class ParserException : Exception
{
    public ParserException(string message) : base(message) { }
}

public class DataOffsetDiscrepancyException : Exception
{
    public DataOffsetDiscrepancyException(string message) : base(message) { }
}

public class MultipleDataSetsException : Exception
{
    public MultipleDataSetsException(string message) : base(message) { }
}

public class UnsupportedVersionException : Exception
{
    public UnsupportedVersionException(string message) : base(message) { }
}
