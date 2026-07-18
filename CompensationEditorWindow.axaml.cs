using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using gated.Models;

namespace gated;

public partial class CompensationEditorWindow : Window
{
    private CompensationMatrix compensation = null!;
    private string[] channels = [];
    private readonly Dictionary<(int Row, int Column), TextBox> inputs = new();

    public CompensationEditorWindow()
    {
        InitializeComponent();
    }

    public CompensationEditorWindow(CompensationMatrix compensation)
    {
        InitializeComponent();
        this.compensation = compensation;
        channels = compensation.ChannelNames.ToArray();
        nameBox.Text = compensation.Name;
        build_matrix();
        cancelButton.Click += (_, _) => Close(false);
        okButton.Click += (_, _) => save_and_close();
    }

    private void build_matrix()
    {
        matrixGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (int column = 0; column < channels.Length; column++)
            matrixGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(84)));
        matrixGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int row = 0; row < channels.Length; row++)
            matrixGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (int column = 0; column < channels.Length; column++)
        {
            var header = new TextBlock
            {
                Text = channels[column],
                Foreground = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Text2")),
                FontWeight = FontWeight.SemiBold,
                TextAlignment = TextAlignment.Center
            };
            Grid.SetColumn(header, column + 1);
            matrixGrid.Children.Add(header);
        }

        for (int row = 0; row < channels.Length; row++)
        {
            var row_header = new TextBlock
            {
                Text = channels[row],
                Foreground = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Text4")),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right,
                Margin = new Avalonia.Thickness(0, 0, 4, 0)
            };
            Grid.SetRow(row_header, row + 1);
            matrixGrid.Children.Add(row_header);

            for (int column = 0; column < channels.Length; column++)
            {
                var input = new TextBox
                {
                    Text = row == column ? "100" : format_percent(compensation.Values[row, column]),
                    MinWidth = 80,
                    IsEnabled = row != column,
                    HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Right
                };
                Grid.SetRow(input, row + 1);
                Grid.SetColumn(input, column + 1);
                matrixGrid.Children.Add(input);
                inputs[(row, column)] = input;
            }
        }
    }

    private void save_and_close()
    {
        var values = new float[channels.Length, channels.Length];
        for (int row = 0; row < channels.Length; row++)
        for (int column = 0; column < channels.Length; column++)
        {
            if (row == column)
            {
                values[row, column] = 1f;
                continue;
            }

            string text = inputs[(row, column)].Text ?? "";
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out float percent) &&
                !float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out percent))
            {
                show_error($"Invalid value at {channels[row]} / {channels[column]}.");
                return;
            }

            values[row, column] = percent / 100f;
        }

        string name = nameBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name))
        {
            show_error("Enter a compensation name.");
            return;
        }

        compensation.Name = name;
        compensation.ReplaceValues(values);
        Close(true);
    }

    private void show_error(string message)
    {
        errorText.Text = message;
        errorText.IsVisible = true;
    }

    private static string format_percent(float value) =>
        (value * 100f).ToString("0.###", CultureInfo.CurrentCulture);
}
