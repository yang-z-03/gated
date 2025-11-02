using System.Collections.Generic;

namespace Gated.Models;

public class Compensation
{
    public Dictionary<Channel, Dictionary<Channel, float>> Matrix { get; set; } = new();
}