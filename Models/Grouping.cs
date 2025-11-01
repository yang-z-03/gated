using System.Collections.Generic;
using System.Collections;
using System.Collections.ObjectModel;

namespace Gated.Models;

public class Grouping : ObservableCollection<Tube>
{
    // a grouping can only consist of tubes that have matched dimensions.
    // thus, operations such as compensation can be performed conveniently.

    public ObservableCollection<Dimension> Dimensions { get; set; } = new();
    public Compensation Compensation { get; set; } = new();
}