using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace gated.Updater;

public sealed class UpdaterWindow : Window
{
    private readonly string[] args;
    private readonly TextBlock title = new()
    {
        Text = "Installing update ...",
        FontWeight = FontWeight.SemiBold,
        Foreground = Brushes.White
    };
    private readonly TextBlock subtitle = new()
    {
        Text = "Preparing update.",
        TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(Color.FromRgb(218, 221, 228))
    };
    private readonly ProgressBar progress = new()
    {
        Minimum = 0,
        Maximum = 100,
        Height = 18
    };

    public UpdaterWindow(string[] args)
    {
        this.args = args;
        Title = "Updating Gated";
        Width = 460;
        Height = 170;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(48, 48, 48));
        Content = new Border
        {
            Padding = new Thickness(22),
            Child = new StackPanel
            {
                Spacing = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { title, subtitle, progress }
            }
        };

        Opened += async (_, _) => await run_update();
    }

    private async Task run_update()
    {
        try
        {
            var options = parse_args(args);
            await Task.Run(() => apply_update(options, report));
            report("Restarting Gated ...", 100);
            Process.Start(new ProcessStartInfo
            {
                FileName = options.AppPath,
                WorkingDirectory = options.InstallRoot,
                UseShellExecute = true
            });
            close_application();
        }
        catch (Exception exception)
        {
            title.Text = "Update failed";
            subtitle.Text = exception.Message;
            progress.IsVisible = false;
        }
    }

    private static void apply_update(UpdateOptions options, Action<string, double> progress)
    {
        wait_for_parent(options.ParentPid);

        string extract_root = Path.Combine(Path.GetTempPath(), "gated-update-extract-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(extract_root);
        try
        {
            var archives = read_archives(options.ManifestPath);
            if (archives.Length == 0)
                throw new InvalidDataException("Update manifest did not contain archives.");

            int archive_index = 0;
            foreach (var archive in archives)
            {
                archive_index++;
                progress($"Extracting archive {archive_index} of {archives.Length} ...", 5 + archive_index * 20.0 / archives.Length);
                string target = safe_combine(extract_root, archive.ExtractPath);
                Directory.CreateDirectory(target);
                ZipFile.ExtractToDirectory(archive.Path, target, overwriteFiles: true);
            }

            progress("Removing old installation files ...", 35);
            clean_installation(options.InstallRoot, options.UpdaterPath);

            var files = Directory.EnumerateFiles(extract_root, "*", SearchOption.AllDirectories).ToArray();
            if (files.Length == 0)
                throw new InvalidDataException("Update archive did not contain files.");

            foreach (string directory in Directory.EnumerateDirectories(extract_root, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(extract_root, directory);
                Directory.CreateDirectory(Path.Combine(options.InstallRoot, relative));
            }

            for (int i = 0; i < files.Length; i++)
            {
                string source = files[i];
                string relative = Path.GetRelativePath(extract_root, source);
                string target = Path.Combine(options.InstallRoot, relative);
                if (is_same_path(target, options.UpdaterPath))
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: true);
                progress($"Copying {relative}", 35 + ((i + 1) * 60.0 / files.Length));
            }
        }
        finally
        {
            try
            {
                Directory.Delete(extract_root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static void wait_for_parent(int parent_pid)
    {
        try
        {
            using var parent = Process.GetProcessById(parent_pid);
            parent.WaitForExit();
        }
        catch
        {
        }
    }

    private static ManifestArchive[] read_archives(string manifest_path)
    {
        var document = XDocument.Load(manifest_path);
        return document.Root?.Elements("archive")
            .Select(element =>
            {
                string path = (string?)element.Attribute("path") ?? throw new InvalidDataException("Archive path is missing.");
                return new ManifestArchive(
                    Path.GetFullPath(path),
                    normalize_extract_path((string?)element.Attribute("extract") ?? "."));
            })
            .ToArray() ?? [];
    }

    private static string normalize_extract_path(string path)
    {
        path = path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        return string.IsNullOrWhiteSpace(path) ? "." : path;
    }

    private static string safe_combine(string root, string relative_path)
    {
        string combined = Path.GetFullPath(Path.Combine(root, relative_path));
        string full_root = Path.GetFullPath(root);
        if (!full_root.EndsWith(Path.DirectorySeparatorChar))
            full_root += Path.DirectorySeparatorChar;

        if (!combined.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) &&
            !combined.StartsWith(full_root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Archive extract path is outside the target directory.");

        return combined;
    }

    private static void clean_installation(string install_root, string updater_path)
    {
        foreach (string file in Directory.EnumerateFiles(install_root))
        {
            if (is_same_path(file, updater_path))
                continue;

            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }

        foreach (string directory in Directory.EnumerateDirectories(install_root))
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            foreach (string child_directory in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(child_directory, FileAttributes.Normal);
            }

            File.SetAttributes(directory, FileAttributes.Normal);
            Directory.Delete(directory, recursive: true);
        }
    }

    private void report(string message, double value)
    {
        Dispatcher.UIThread.Post(() =>
        {
            subtitle.Text = message;
            progress.Value = Math.Clamp(value, 0, 100);
        });
    }

    private static UpdateOptions parse_args(string[] args)
    {
        string manifest = read_option(args, "--manifest");
        string app = read_option(args, "--app");
        string updater = read_option(args, "--updater");
        string parent = read_option(args, "--parent-pid");
        if (!int.TryParse(parent, NumberStyles.None, CultureInfo.InvariantCulture, out int parent_pid))
            throw new ArgumentException("Invalid parent process id.");

        string install_root = Path.GetDirectoryName(app) ?? throw new ArgumentException("Invalid application path.");
        return new UpdateOptions(
            Path.GetFullPath(manifest),
            Path.GetFullPath(app),
            Path.GetFullPath(updater),
            Path.GetFullPath(install_root),
            parent_pid);
    }

    private static string read_option(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
            throw new ArgumentException($"Missing {name}.");

        return args[index + 1];
    }

    private static bool is_same_path(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static void close_application()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
        else
            Environment.Exit(0);
    }
}

internal sealed record UpdateOptions(
    string ManifestPath, 
    string AppPath, 
    string UpdaterPath, 
    string InstallRoot, 
    int ParentPid
);

internal sealed record ManifestArchive(string Path, string ExtractPath);
