using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Controls.Shapes;

namespace gated;

public partial class AboutWindow : Window
{
    private const int PixelScale = 4;
    private const int EasterEggClickThreshold = 10;
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan FadeInterval = TimeSpan.FromMilliseconds(16);

    private readonly DispatcherTimer pixel_timer;
    private readonly List<Rectangle> pixel_rectangles = [];
    private int icon_clicks;
    private int pixel_frame_index;
    private bool easter_egg_running;

    public AboutWindow()
    {
        InitializeComponent();
        var ver = GetType().Assembly.GetName().Version;
        this.version.Content = $"Version {ver!.Major}.{ver!.Minor} " + (
            ver.Build > 0 ? $"Patch {ver.Build}" : ""
        );

        pixel_timer = new DispatcherTimer { Interval = FrameInterval };
        pixel_timer.Tick += pixel_timer_tick;
    }

    private void ok_button_click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void about_icon_pointer_pressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(AboutIcon).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (easter_egg_running)
        {
            return;
        }

        icon_clicks++;
        if (icon_clicks < EasterEggClickThreshold)
        {
            return;
        }

        icon_clicks = 0;
        e.Handled = true;
        await play_easter_egg_async();
    }

    private async Task play_easter_egg_async()
    {
        easter_egg_running = true;
        pixel_timer.Stop();

        await fade_async(TitlePanel, from: 1, to: 0, milliseconds: 450);
        TitlePanel.IsVisible = false;

        PixelCanvas.IsVisible = true;
        PixelCanvas.Opacity = 0;
        pixel_frame_index = 0;
        render_pixel_frame(pixel_frame_index);
        await fade_async(PixelCanvas, from: 0, to: 1, milliseconds: 220);

        pixel_timer.Start();
    }

    private async void pixel_timer_tick(object? sender, EventArgs e)
    {
        pixel_frame_index++;
        if (pixel_frame_index < PixelFrameCount)
        {
            render_pixel_frame(pixel_frame_index);
            return;
        }

        pixel_timer.Stop();
        await fade_async(PixelCanvas, from: 1, to: 0, milliseconds: 300);
        PixelCanvas.IsVisible = false;

        TitlePanel.IsVisible = true;
        await fade_async(TitlePanel, from: 0, to: 1, milliseconds: 450);
        easter_egg_running = false;
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
        pixel_rectangles.Clear();

        foreach (var pixel in decode_pixel_frame(frame_index))
        {
            var rectangle = new Rectangle
            {
                Width = PixelScale,
                Height = PixelScale,
                Fill = new SolidColorBrush(Color.FromRgb(pixel.R, pixel.G, pixel.B))
            };

            Canvas.SetLeft(rectangle, pixel.X * PixelScale);
            Canvas.SetTop(rectangle, pixel.Y * PixelScale);
            PixelCanvas.Children.Add(rectangle);
            pixel_rectangles.Add(rectangle);
        }
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
        for (int i = 0; i + 4 < bytes.Length; i += 5)
        {
            yield return new PixelCell(bytes[i], bytes[i + 1], bytes[i + 2], bytes[i + 3], bytes[i + 4]);
        }
    }

    private readonly record struct PixelCell(byte X, byte Y, byte R, byte G, byte B);
}
