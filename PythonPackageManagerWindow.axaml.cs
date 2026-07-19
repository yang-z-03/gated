using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Svg.Skia;
using Avalonia.Threading;
using gated.Shared;

namespace gated;

public partial class PythonPackageManagerWindow : Window
{
    private static readonly IBrush RequiredBrush = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Text2"));
    private static readonly IBrush OptionalBrush = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Text3"));
    private static readonly IBrush MissingRequiredBrush = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Text5"));

    private readonly ObservableCollection<PythonPackageRow> packages = new();
    private readonly string python_executable;

    public PythonPackageManagerWindow()
    {
        InitializeComponent();
        python_executable = PlatformSupport.EmbeddedPythonExecutablePath();
        packageList.ItemsSource = packages;
        refreshButton.Click += async (_, _) => await refresh_packages_async();
        installCustomButton.Click += async (_, _) => await install_custom_package_async();
        closeButton.Click += (_, _) => Close();
        Opened += async (_, _) => await refresh_packages_async();
    }

    private async Task refresh_packages_async()
    {
        if (!File.Exists(python_executable))
        {
            packages.Clear();
            return;
        }

        set_busy(true);
        try
        {
            var list_result = await run_pip_async(["list", "--format=json", "--disable-pip-version-check"], show_output: false);
            if (list_result.ExitCode != 0)
                return;

            var installed = JsonSerializer.Deserialize<PipPackage[]>(list_result.StdOut,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            var details = await load_package_details_async(installed.Select(package => package.Name).ToArray());
            rebuild_rows(installed, details);
        }
        finally
        {
            set_busy(false);
        }
    }

    private async Task<Dictionary<string, PipPackageDetails>> load_package_details_async(string[] package_names)
    {
        if (package_names.Length == 0)
            return new Dictionary<string, PipPackageDetails>(StringComparer.OrdinalIgnoreCase);

        var result = await run_pip_async(["show", .. package_names], show_output: false);
        return parse_pip_show(result.StdOut);
    }

    private void rebuild_rows(PipPackage[] installed, Dictionary<string, PipPackageDetails> details)
    {
        packages.Clear();
        var installed_by_name = installed
            .GroupBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var required_names = PlatformSupport.RequiredPythonPackages
            .Select(package => package.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var required_by = build_required_by(details);

        foreach (var required in PlatformSupport.RequiredPythonPackages)
        {
            installed_by_name.TryGetValue(required.Name, out var package);
            details.TryGetValue(required.Name, out var detail);
            required_by.TryGetValue(required.Name, out var dependents);
            packages.Add(new PythonPackageRow(
                required.Name,
                package?.Version ?? "",
                required.VersionCondition,
                is_required: true,
                detail?.Requires ?? [],
                dependents ?? []));
        }

        foreach (var package in installed.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (required_names.Contains(package.Name))
                continue;

            details.TryGetValue(package.Name, out var detail);
            required_by.TryGetValue(package.Name, out var dependents);
            packages.Add(new PythonPackageRow(
                package.Name,
                package.Version,
                "",
                is_required: false,
                detail?.Requires ?? [],
                dependents ?? []));
        }
    }

    private static Dictionary<string, string[]> build_required_by(Dictionary<string, PipPackageDetails> details)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in details.Values)
        {
            foreach (string dependency in package.Requires)
            {
                if (!result.TryGetValue(dependency, out var dependents))
                {
                    dependents = new List<string>();
                    result[dependency] = dependents;
                }
                dependents.Add(package.Name);
            }
        }

        return result.ToDictionary(
            item => item.Key,
            item => item.Value.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private async void install_or_uninstall_package_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not PythonPackageRow row)
            return;

        string spec = row.Name + row.VersionCondition;
        string[] args;
        if (row.IsInstalled)
        {
            string[] packages_to_remove = packages_required_by(row.Name);
            bool confirmed = await confirm_uninstall_async(packages_to_remove);
            if (!confirmed)
                return;

            args = ["uninstall", "-y", .. packages_to_remove, "--disable-pip-version-check"];
        }
        else
        {
            args = ["install", spec, "--disable-pip-version-check"];
        }

        await run_package_operation_async(args);
    }

    private string[] packages_required_by(string package_name)
    {
        var rows_by_name = packages
            .Where(package => package.IsInstalled)
            .ToDictionary(package => package.Name, StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>();
        pending.Enqueue(package_name);

        while (pending.Count > 0)
        {
            string current = pending.Dequeue();
            if (!visited.Add(current))
                continue;

            result.Add(current);
            if (!rows_by_name.TryGetValue(current, out var row))
                continue;

            foreach (string dependent in row.RequiredBy)
            {
                if (rows_by_name.ContainsKey(dependent))
                    pending.Enqueue(dependent);
            }
        }

        return result
            .OrderBy(package => package.Equals(package_name, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(package => package, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void depends_package_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is PythonPackageRow row)
            _ = show_list_dialog_async($"{row.Name} depends on", row.Requires);
    }

    private void required_by_package_click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is PythonPackageRow row)
            _ = show_list_dialog_async($"{row.Name} is required by", row.RequiredBy);
    }

    private async Task install_custom_package_async()
    {
        string package_name = (packageNameBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(package_name))
            return;

        string version_condition = (versionConditionBox.Text ?? "").Trim();
        string index_url = (indexUrlBox.Text ?? "").Trim();
        var args = new Collection<string>
        {
            "install",
            package_name + version_condition,
            "--disable-pip-version-check"
        };
        if (!string.IsNullOrWhiteSpace(index_url))
        {
            args.Add("-i");
            args.Add(index_url);
        }

        await run_package_operation_async(args.ToArray());
    }

    private async Task run_package_operation_async(string[] pip_args)
    {
        if (!File.Exists(python_executable))
        {
            return;
        }

        set_busy(true);
        clear_output();
        try
        {
            await run_pip_async(pip_args, show_output: true);
            await refresh_packages_async();
        }
        finally
        {
            set_busy(false);
        }
    }

    private async Task<PipResult> run_pip_async(string[] pip_args, bool show_output)
    {
        var output = new StringBuilder();
        var error = new StringBuilder();
        var start = new ProcessStartInfo
        {
            FileName = python_executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-m");
        start.ArgumentList.Add("pip");
        foreach (string arg in pip_args)
            start.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            output.AppendLine(e.Data);
            if (show_output)
                append_output(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            error.AppendLine(e.Data);
            if (show_output)
                append_output(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        return new PipResult(process.ExitCode, output.ToString(), error.ToString());
    }

    private void clear_output()
    {
        pipOutputText.Text = "";
    }

    private void append_output(string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            pipOutputText.Text += (pipOutputText.Text?.Length > 0 ? Environment.NewLine : "") + text;
            pipOutputScroll.ScrollToEnd();
        });
    }

    private async Task show_list_dialog_async(string title, IReadOnlyList<string> items)
    {
        string text = items.Count == 0 ? "None" : string.Join(Environment.NewLine, items);
        var dialog = new Window
        {
            Title = title,
            Width = 360,
            MinWidth = 300,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(gated.Shared.ThemeResources.AppColor(this, "WindowBackground")),
            Content = new Grid
            {
                Margin = new Avalonia.Thickness(16),
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto)
                },
                RowSpacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = text,
                        FontSize = 13,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Text3"))
                    },
                    new Button
                    {
                        Content = "OK",
                        Classes = { "Small" },
                        MinWidth = 80,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
                    }
                }
            }
        };
        if (dialog.Content is Grid grid && grid.Children[1] is Button ok)
        {
            Grid.SetRow(ok, 1);
            ok.Click += (_, _) => dialog.Close();
        }
        await dialog.ShowDialog(this);
    }

    private async Task<bool> confirm_uninstall_async(IReadOnlyList<string> packages_to_remove)
    {
        string text = string.Join(Environment.NewLine, packages_to_remove);
        var remove_button = new Button
        {
            Content = "Remove",
            Classes = { "Small", "Danger" },
            MinWidth = 82
        };
        var cancel_button = new Button
        {
            Content = "Cancel",
            Classes = { "Small" },
            MinWidth = 82
        };
        var dialog = new Window
        {
            Title = "Remove packages",
            Width = 420,
            MinWidth = 340,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(gated.Shared.ThemeResources.AppColor(this, "WindowBackground")),
            Content = new Grid
            {
                Margin = new Avalonia.Thickness(16),
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto)
                },
                RowSpacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = "The following packages will be removed:",
                        FontSize = 13,
                        Foreground = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Text3"))
                    },
                    new Border
                    {
                        Background = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Background3")),
                        BorderBrush = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Border3")),
                        BorderThickness = new Avalonia.Thickness(1),
                        CornerRadius = new Avalonia.CornerRadius(4),
                        Padding = new Avalonia.Thickness(8),
                        Child = new TextBlock
                        {
                            Text = text,
                            FontSize = 13,
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = new SolidColorBrush(gated.Shared.ThemeResources.AppColor("Text3"))
                        }
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Children =
                        {
                            cancel_button,
                            remove_button
                        }
                    }
                }
            }
        };

        if (dialog.Content is Grid grid)
        {
            Grid.SetRow(grid.Children[1], 1);
            Grid.SetRow(grid.Children[2], 2);
        }
        cancel_button.Click += (_, _) => dialog.Close(false);
        remove_button.Click += (_, _) => dialog.Close(true);
        bool? result = await dialog.ShowDialog<bool?>(this);
        return result == true;
    }

    private void set_busy(bool value)
    {
        refreshButton.IsEnabled = !value;
        installCustomButton.IsEnabled = !value;
        packageList.IsEnabled = !value;
    }

    private static Dictionary<string, PipPackageDetails> parse_pip_show(string text)
    {
        var result = new Dictionary<string, PipPackageDetails>(StringComparer.OrdinalIgnoreCase);
        string name = "";
        string[] requires = [];

        void flush()
        {
            if (!string.IsNullOrWhiteSpace(name))
                result[name] = new PipPackageDetails(name, requires);
        }

        foreach (string line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("---", StringComparison.Ordinal))
            {
                flush();
                name = "";
                requires = [];
                continue;
            }

            int separator = line.IndexOf(':');
            if (separator <= 0)
                continue;

            string key = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();
            if (string.Equals(key, "Name", StringComparison.OrdinalIgnoreCase))
                name = value;
            else if (string.Equals(key, "Requires", StringComparison.OrdinalIgnoreCase))
                requires = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }
        flush();
        return result;
    }

    private sealed record PipPackage(string Name, string Version);

    private sealed record PipPackageDetails(string Name, string[] Requires);

    private sealed record PipResult(int ExitCode, string StdOut, string StdErr);

    private sealed class PythonPackageRow
    {
        public PythonPackageRow(
            string name,
            string version,
            string version_condition,
            bool is_required,
            IReadOnlyList<string> requires,
            IReadOnlyList<string> required_by)
        {
            Name = name;
            Version = version;
            VersionCondition = version_condition;
            IsRequired = is_required;
            Requires = requires;
            RequiredBy = required_by;
        }

        public string Name { get; }
        public string Version { get; }
        public string VersionCondition { get; }
        public bool IsRequired { get; }
        public IReadOnlyList<string> Requires { get; }
        public IReadOnlyList<string> RequiredBy { get; }
        public bool IsInstalled => !string.IsNullOrWhiteSpace(Version);
        public string VersionText => IsInstalled ? Version : "";
        public FontWeight NameWeight => IsRequired ? FontWeight.Bold : FontWeight.Normal;
        public IBrush NameBrush => IsRequired && !IsInstalled
            ? MissingRequiredBrush
            : IsRequired
                ? RequiredBrush
                : OptionalBrush;
        public string InstallOrUninstallIcon => IsInstalled ? "uninstall.svg" : "install.svg";
        public string InstallOrUninstallTip => IsInstalled ? "Uninstall package" : "Install package";
    }
}
