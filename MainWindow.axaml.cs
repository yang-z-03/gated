
using System;
using Avalonia.Controls;
using ScottPlot.Avalonia;
using ScottPlot;

namespace Gated;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        
        double[] dataX = { 1, 2, 3, 4, 5 };
        double[] dataY = { 1, 4, 9, 16, 25 };

        AvaPlot myPlot = this.Find<AvaPlot>("AvaPlot1") ?? throw new NullReferenceException();
        // set the color palette used when coloring new items added to the plot
        myPlot.Plot.Add.Palette = new ScottPlot.Palettes.Penumbra();
        myPlot.Plot.Add.Scatter(dataX, dataY);
        // change figure colors
        myPlot.Plot.FigureBackground.Color = Color.FromHex("#181818");
        myPlot.Plot.DataBackground.Color = Color.FromHex("#1f1f1f");

        // change axis and grid colors
        myPlot.Plot.Axes.Color(Color.FromHex("#d7d7d7"));
        myPlot.Plot.Grid.MajorLineColor = Color.FromHex("#404040");

        // change legend colors
        myPlot.Plot.Legend.BackgroundColor = Color.FromHex("#404040");
        myPlot.Plot.Legend.FontColor = Color.FromHex("#d7d7d7");
        myPlot.Plot.Legend.OutlineColor = Color.FromHex("#d7d7d7");
        myPlot?.Refresh();
    }
}