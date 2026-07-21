using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using gated.Shared;
using gated.ViewModels;

namespace gated.Controls;

public abstract class DemultiplexDataGridBase : DataGrid
{
    public static readonly StyledProperty<IndexDemultiplexViewModel?> PanelProperty =
        AvaloniaProperty.Register<DemultiplexDataGridBase, IndexDemultiplexViewModel?>(nameof(Panel));

    private IndexDemultiplexViewModel? subscribed_panel;

    protected override Type StyleKeyOverride => typeof(DataGrid);

    protected DemultiplexDataGridBase()
    {
        AutoGenerateColumns = false;
        CanUserResizeColumns = true;
        CanUserReorderColumns = false;
        GridLinesVisibility = DataGridGridLinesVisibility.None;
        HeadersVisibility = DataGridHeadersVisibility.Column;
        SelectionMode = DataGridSelectionMode.Single;
        RowHeight = 28;
    }

    public IndexDemultiplexViewModel? Panel { get => GetValue(PanelProperty); set => SetValue(PanelProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != PanelProperty) return;
        if (subscribed_panel is not null) subscribed_panel.StructureChanged -= panel_structure_changed;
        subscribed_panel = Panel;
        if (subscribed_panel is not null) subscribed_panel.StructureChanged += panel_structure_changed;
        Rebuild();
    }

    private void panel_structure_changed(object? sender, EventArgs e) => Rebuild();
    protected abstract void Rebuild();

    protected static DataGridTemplateColumn remove_column(string tooltip)
    {
        return new DataGridTemplateColumn
        {
            Header = "",
            Width = new DataGridLength(52),
            CanUserResize = false,
            CellTemplate = new FuncDataTemplate<IndexDemultiplexSampleRowViewModel>((_, _) =>
            {
                var icon = new ThemeIcon { Width = 15, Height = 15, Icon = "delete.svg" };
                var button = new Button
                {
                    Content = icon,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                ToolTip.SetTip(button, tooltip);
                button.Classes.Add("Small");
                button.Bind(Button.CommandProperty, new Binding(nameof(IndexDemultiplexSampleRowViewModel.RemoveCommand)));
                button.Bind(Button.CommandParameterProperty, new Binding("."));
                return button;
            })
        };
    }
}

public sealed class DemultiplexSampleDataGrid : DemultiplexDataGridBase
{
    public DemultiplexSampleDataGrid() => Rebuild();

    protected override void Rebuild()
    {
        Columns.Clear();
        ItemsSource = Panel?.Rows;
        Columns.Add(new DataGridTemplateColumn
        {
            Header = "Sample",
            Width = DataGridLength.SizeToHeader,
            MinWidth = 150,
            CellTemplate = new FuncDataTemplate<IndexDemultiplexSampleRowViewModel>((_, _) =>
            {
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 7,
                    VerticalAlignment = VerticalAlignment.Center
                };
                row.Classes.Add("IconRow");
                row.Children.Add(new ThemeIcon { Width = 15, Height = 15, Icon = "tube.svg" });
                var name = new TextBlock
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                name.Bind(TextBlock.TextProperty, new Binding(nameof(IndexDemultiplexSampleRowViewModel.SampleName)));
                row.Children.Add(name);
                return row;
            })
        });
        if (Panel is not null)
            for (int index = 0; index < Panel.Channels.Count; index++)
                Columns.Add(new DataGridTextColumn
                {
                    Header = $"{Panel.Channels[index].Name} cutoff",
                    Binding = new Binding($"Cutoffs[{index}].ValueText") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus },
                    Width = DataGridLength.SizeToHeader,
                    MinWidth = 110
                });
        Columns.Add(new DataGridTextColumn
        {
            Header = "Status",
            Binding = new Binding(nameof(IndexDemultiplexSampleRowViewModel.StatusText)),
            IsReadOnly = true,
            Width = DataGridLength.SizeToHeader,
            MinWidth = 80
        });
        Columns.Add(remove_column("Remove sample"));
    }
}

public sealed class DemultiplexSubsetDataGrid : DemultiplexDataGridBase
{
    public DemultiplexSubsetDataGrid() => Rebuild();

    protected override void Rebuild()
    {
        Columns.Clear();
        ItemsSource = Panel?.VisibleSubsets;
        Columns.Add(new DataGridCheckBoxColumn
        {
            Header = "",
            Binding = new Binding(nameof(IndexDemultiplexSubsetViewModel.IsIncluded)) { Mode = BindingMode.TwoWay },
            Width = new DataGridLength(40),
            MinWidth = 40
        });
        Columns.Add(new DataGridTextColumn
        {
            Header = "Subset",
            Binding = new Binding(nameof(IndexDemultiplexSubsetViewModel.Name)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus },
            Width = new DataGridLength(110),
            MinWidth = 80
        });
        if (Panel is not null)
        {
            for (int index = 0; index < Panel.Channels.Count; index++)
                Columns.Add(new DataGridTextColumn
                {
                    Header = Panel.Channels[index].Name,
                    Binding = new Binding($"Signs[{index}].Value"),
                    IsReadOnly = true,
                    Width = DataGridLength.SizeToHeader
                });
            for (int index = 0; index < Panel.Rows.Count; index++)
                Columns.Add(new DataGridTextColumn
                {
                    Header = Panel.Rows[index].SampleName,
                    Binding = new Binding($"Counts[{index}].Value"),
                    IsReadOnly = true,
                    Width = DataGridLength.SizeToHeader
                });
        }
    }
}
