using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Shapes;
using gated.Shared;

namespace gated;

public partial class AboutWindow : Window
{
    private const int PixelScale = 4;
    private const int KnockLimit = 10;
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(100);
    private static readonly int[] Ledger =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9,
        10, 11, 10, 11, 10, 11,
        13, 14, 13, 14, 13, 14, 13, 14,
        15, 16, 17, 18, 19,
        20, 21, 20, 21,
        22, 23
    ];
    private static readonly Random Abacus = new();

    private readonly List<Rectangle> paperclips = [];
    private int[] catalog = [];
    private int receipts;
    private bool audit;
    private bool sealed_packet;

    public AboutWindow()
    {
        InitializeComponent();
        var ver = GetType().Assembly.GetName().Version;
        this.version.Content = $"Version {ver!.Major}.{ver!.Minor} " + (
            ver.Build > 0 ? $"Patch {ver.Build}" : ""
        );

    }

    private void ok_button_click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void path_info_button_click(object? sender, RoutedEventArgs e)
    {
        await show_path_info_dialog();
    }

    private async Task show_path_info_dialog()
    {
        var content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 5
        };

        add_path_section(content, "Application", [
            ("Base directory", AppContext.BaseDirectory),
            ("Persistence directory", PlatformSupport.PersistenceDirectory)
        ]);
        add_path_section(content, "Update and settings", [
            ("Updater", PlatformSupport.UpdaterPath),
            ("Update metadata", PlatformSupport.UpdateMetadataPath),
            ("Macros", PlatformSupport.MacroDirectory),
            ("Statistics", PlatformSupport.StatisticDirectory)
        ]);
        add_path_section(content, "Python", [
            ("Python home", PlatformSupport.EmbeddedPythonHome()),
            ("Python DLL", PlatformSupport.EmbeddedPythonLibraryPath()),
            ("Python executable", PlatformSupport.EmbeddedPythonExecutablePath())
        ]);
        add_system_path_section(content, "System PATH", Environment.GetEnvironmentVariable("PATH"));
        add_system_path_section(content, "PYTHONHOME", Environment.GetEnvironmentVariable("PYTHONHOME"));

        var ok = new Button
        {
            Content = "OK",
            MinWidth = 80,
            IsDefault = true,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(16, 0, 16, 16)
        };

        ok.Classes.Add("Small");

        var dialog = new Window
        {
            Title = "Configured paths",
            Width = 680,
            Height = 520,
            MinWidth = 520,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background,
            Content = new Grid
            {
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Star),
                    new RowDefinition(GridLength.Auto)
                },
                Children =
                {
                    new ScrollViewer
                    {
                        Padding = new Avalonia.Thickness(0),
                        Margin = new Avalonia.Thickness(8),
                        Content = content
                    },
                    ok
                }
            }
        };

        if (dialog.Content is Grid grid && grid.Children[1] is Button ok_button)
        {
            Grid.SetRow(grid.Children[1], 1);
            ok_button.Click += (_, _) => dialog.Close();
        }

        await dialog.ShowDialog(this);
    }

    private static void add_path_section(StackPanel panel, string title, IReadOnlyList<(string Name, string Path)> paths)
    {
        panel.Children.Add(section_title(title));
        foreach (var path in paths)
        {
            panel.Children.Add(new TextBlock
            {
                Text = path.Name,
                Foreground = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Text3")),
                FontWeight = FontWeight.SemiBold,
                FontSize = 12
            });
            panel.Children.Add(code_text(path.Path));
        }
    }

    private static void add_system_path_section(StackPanel panel, string title, string? value)
    {
        panel.Children.Add(section_title(title));
        var paths = (value ?? "")
            .Split(PlatformSupport.EnvironmentPathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (paths.Length == 0)
        {
            panel.Children.Add(code_text("(not set)"));
            return;
        }

        foreach (string path in paths)
            panel.Children.Add(code_text(path));
    }

    private static TextBlock section_title(string title) =>
        new()
        {
            Text = title,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Text2")),
            FontSize = 13,
            Margin = new Avalonia.Thickness(0, 8, 0, 0)
        };

    private static TextBlock code_text(string text) =>
        new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = FontFamily.Parse("avares://gated/Fonts#IBM Plex Mono"),
            FontSize = 12,
            LineHeight = 17,
            Foreground = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Text4"))
        };

    private async void about_icon_pointer_pressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(AboutIcon).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (audit || sealed_packet)
        {
            return;
        }

        receipts++;
        if (receipts < KnockLimit)
        {
            return;
        }

        receipts = 0;
        e.Handled = true;
        await reconcile_async();
    }

    private async Task reconcile_async()
    {
        audit = true;
        sealed_packet = true;
        var catalogs = collect_ledgers();
        catalog = catalogs[Abacus.Next(catalogs.Length)];

        PixelCanvas.IsVisible = true;
        PixelCanvas.Opacity = 0;
        _ = tint_async(HeaderRack, gated.Shared.ThemeResources.AppColor("Background2"), Colors.Black, milliseconds: 450);

        render_pixel_frame(Ledger[0]);
        await fade_async(PixelCanvas, from: 0, to: 1, milliseconds: 220);

        for (int i = 1; i < Ledger.Length; i++)
        {
            await Task.Delay(FrameInterval);
            render_pixel_frame(Ledger[i]);
        }

        await Task.Delay(FrameInterval);
        render_pixel_frame(PixelFrameCount - 1);
        audit = false;
    }

    private static async Task fade_async(Control control, double from, double to, int milliseconds)
    {
        const int steps = 18;
        control.Opacity = from;

        for (int i = 1; i <= steps; i++)
        {
            double t = i / (double)steps;
            control.Opacity = from + (to - from) * t;
            await Task.Delay(Math.Max(1, milliseconds / steps));
        }

        control.Opacity = to;
    }

    private void render_pixel_frame(int frame_index)
    {
        PixelCanvas.Children.Clear();
        paperclips.Clear();

        foreach (var pixel in decode_pixel_frame(frame_index))
        {
            var rectangle = new Rectangle
            {
                Width = PixelScale,
                Height = PixelScale,
                Fill = new SolidColorBrush(resolve_color(pixel.Tone))
            };

            Canvas.SetLeft(rectangle, pixel.X * PixelScale);
            Canvas.SetTop(rectangle, pixel.Y * PixelScale);
            PixelCanvas.Children.Add(rectangle);
            paperclips.Add(rectangle);
        }
    }

    private Color resolve_color(byte tone)
    {
        var active_catalog = catalog.Length > 0 ? catalog : AboutPixelColorKeys;
        var source = tone < active_catalog.Length ? active_catalog[tone] : AboutPixelColorKeys[tone];
        return Color.FromRgb((byte)(source >> 16), (byte)(source >> 8), (byte)source);
    }

    private static int[][] collect_ledgers()
    {
        return
        [
            AboutPixelColorKeys, // balbc
            [0x6b6b6b, 0xad877c, 0x4f4f4f, 0xFFDBB6, 0x303030, 0xe8e8e8, 0x7d7d7d, 0x000000, 0xffffff, 0xa19473, 0x716151, 0xc78e8e, 0x715951, 0xcfadad, 0xFF9E7D, 0x636363, 0x383838, 0x454545, 0x713845], // b6
            [0x805832, 0xad877c, 0xa15e1e, 0xFFDBB6, 0x4f2813, 0xe8e8e8, 0xa6833a, 0x6b4b33, 0xffffff, 0xa19473, 0x716151, 0xc78e8e, 0x715951, 0xcfadad, 0xFF9E7D, 0xa26940, 0x383838, 0x454545, 0x713845], // c3h
        ];
    }

    private static IEnumerable<PixelCell> decode_pixel_frame(int frame_index)
    {
        var compressed = AboutPixelFrameData[frame_index];
        var compressed_bytes = new byte[compressed.Length];
        for (int i = 0; i < compressed.Length; i++)
        {
            compressed_bytes[i] = (byte)compressed[i];
        }

        using var input = new MemoryStream(compressed_bytes);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);

        var bytes = output.ToArray();
        for (int i = 0; i + 2 < bytes.Length; i += 3)
        {
            yield return new PixelCell(bytes[i], bytes[i + 1], bytes[i + 2]);
        }
    }

    private static async Task tint_async(Panel control, Color from, Color to, int milliseconds)
    {
        const int steps = 18;
        var brush = control.Background as SolidColorBrush ?? new SolidColorBrush(from);
        control.Background = brush;

        for (int i = 1; i <= steps; i++)
        {
            double t = i / (double)steps;
            brush.Color = Color.FromRgb(
                (byte)Math.Round(from.R + ((to.R - from.R) * t)),
                (byte)Math.Round(from.G + ((to.G - from.G) * t)),
                (byte)Math.Round(from.B + ((to.B - from.B) * t)));
            await Task.Delay(Math.Max(1, milliseconds / steps));
        }

        brush.Color = to;
    }

    private readonly record struct PixelCell(byte X, byte Y, byte Tone);
}
