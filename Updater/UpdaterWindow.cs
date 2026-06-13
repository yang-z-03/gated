using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace gated.Updater;

public sealed class UpdaterWindow : Window
{
    private const string default_versions_url = "https://raw.githubusercontent.com/yang-z-03/gated/refs/heads/master/.github/versions";
    private static readonly HttpClient http_client = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly string[] args;
    private readonly TextBlock title = text("Checking for updates", 20, FontWeight.SemiBold);
    private readonly TextBlock subtitle = text("Preparing updater.", 13);
    private readonly StackPanel steps = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly ContentControl page_host = new();
    private readonly Button back_button = new() { Content = "Back", IsEnabled = false };
    private readonly Button cancel_button = new() { Content = "Cancel" };
    private readonly Button next_button = new() { Content = "Next", Classes = { "Primary" }, IsEnabled = false };
    private readonly ProgressBar progress = new() { Minimum = 0, Maximum = 100, Height = 8 };
    private readonly TextBlock progress_detail = text("", 12);

    private UpdateOptions? options;
    private UpdatePlan? plan;
    private int page_index;

    public UpdaterWindow(string[] args)
    {
        this.args = args;
        Title = "Gated Updater";
        Width = 760;
        Height = 560;
        MinWidth = 680;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = brush(31, 34, 40);

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Margin = new Thickness(24),
            Children =
            {
                new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        title,
                        subtitle,
                        steps
                    }
                },
                page_host,
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"),
                    Margin = new Thickness(0, 18, 0, 0),
                    Children =
                    {
                        new StackPanel
                        {
                            Spacing = 5,
                            Children = { progress, progress_detail }
                        },
                        back_button,
                        cancel_button,
                        next_button
                    }
                }
            }
        };
        Grid.SetRow(page_host, 1);
        Grid.SetRow(((Grid)Content).Children[2], 2);
        Grid.SetColumn(back_button, 1);
        Grid.SetColumn(cancel_button, 2);
        Grid.SetColumn(next_button, 3);
        back_button.Margin = new Thickness(0, 0, 8, 0);
        cancel_button.Margin = new Thickness(0, 0, 8, 0);

        back_button.Click += (_, _) => show_page(Math.Max(0, page_index - 1));
        cancel_button.Click += (_, _) => close_application();
        next_button.Click += async (_, _) => await advance();
        Opened += async (_, _) => await initialize();
    }

    private async Task initialize()
    {
        try
        {
            options = parse_args(args);
            set_busy("Checking metadata", "Loading version manifest.", null);
            plan = await Task.Run(() => build_plan(options, report));
            if (plan.TargetVersion.CompareTo(options.CurrentVersion) <= 0 && options.TargetVersion is null)
            {
                set_ready("Gated is up to date", $"Installed version {options.CurrentVersion} is current.");
                next_button.Content = "Close";
                next_button.IsEnabled = true;
                return;
            }

            show_page(0);
        }
        catch (Exception exception)
        {
            show_failure(exception);
        }
    }

    private async Task advance()
    {
        if (next_button.Content?.ToString() == "Close")
        {
            close_application();
            return;
        }

        if (page_index == 0)
        {
            show_page(1);
            return;
        }

        if (page_index == 1)
        {
            show_page(2);
            await run_install();
        }
    }

    private void show_page(int index)
    {
        page_index = index;
        render_steps(index);
        back_button.IsEnabled = index == 1;
        cancel_button.IsEnabled = index < 2;
        next_button.IsEnabled = index < 2;
        next_button.Content = index == 1 ? "Install" : "Next";

        if (plan is null)
            return;

        if (index == 0)
        {
            title.Text = $"Gated {plan.TargetVersion} is ready";
            subtitle.Text = "Required archives have been staged. Review the planned installation before applying it.";
            page_host.Content = build_summary_page(plan);
            set_ready("Downloads ready", "All required update archives are available locally.");
        }
        else if (index == 1)
        {
            title.Text = "Review changes";
            subtitle.Text = "Confirm package, Python, macro, statistic, and pip changes.";
            page_host.Content = build_review_page(plan);
            set_ready("Waiting for confirmation", "No installation files have been replaced yet.");
        }
        else
        {
            title.Text = "Installing update";
            subtitle.Text = "Replacing application files and reconciling Python packages.";
            page_host.Content = build_progress_page();
            back_button.IsEnabled = false;
            next_button.IsEnabled = false;
        }
    }

    private async Task run_install()
    {
        if (options is null || plan is null)
            return;

        try
        {
            await Task.Run(() => apply_update(options, plan, report));
            set_busy("Restarting Gated", "Launching the updated application.", 100);
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
            show_failure(exception);
        }
    }

    private static UpdatePlan build_plan(UpdateOptions options, Action<string, double?> progress)
    {
        wait_for_parent(options.ParentPid, quick: true);
        progress("Reading installed metadata.", null);
        var installed = InstalledState.Load(options.MetadataPath);
        var versions = load_versions(options);
        var system = SystemInfo.Current();
        var target = versions
            .Where(version => version.IsCompatibleWith(system))
            .Where(version => options.TargetVersion is null || version.Version.CompareTo(options.TargetVersion.Value) == 0)
            .OrderBy(version => version.Version)
            .LastOrDefault() ?? throw new InvalidDataException("No compatible update version was found.");

        string staging_root = Path.Combine(Path.GetTempPath(), "gated-update-stage-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(staging_root);

        var staged_archives = download_archives(target.Archives, staging_root, "base", progress);
        var python = target.Requirements.FirstOrDefault(requirement => requirement.Type == "package" && requirement.Name == "python");
        bool python_required = python is not null && (python.OverrideIfExist || !installed.HasRequirement(python.InstallKey));
        StagedArchive? staged_python = null;
        if (python_required && python is not null && python.Href is not null)
            staged_python = download_archives([new UpdateArchive(python.Href, python.ExtractPath)], staging_root, "python", progress).Single();

        var protected_items = scan_protected(options.InstallRoot, target.ProtectedPaths);
        var packages = python?.PythonPackages.Select(package =>
        {
            var installed_package = installed.PythonPackages.FirstOrDefault(item => string.Equals(item.Name, package.Name, StringComparison.OrdinalIgnoreCase));
            bool satisfied = installed_package is not null && version_satisfies(installed_package.Version, package.VersionRange);
            return new PythonPackagePlan(package.Name, package.VersionRange, installed_package?.Version, satisfied);
        }).ToArray() ?? [];

        return new UpdatePlan(target, staging_root, staged_archives, staged_python, python, python_required, protected_items, packages);
    }

    private static void apply_update(UpdateOptions options, UpdatePlan plan, Action<string, double?> progress)
    {
        wait_for_parent(options.ParentPid, quick: false);
        string extract_root = Path.Combine(Path.GetTempPath(), "gated-update-extract-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        string preserve_root = Path.Combine(Path.GetTempPath(), "gated-update-preserve-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(extract_root);
        Directory.CreateDirectory(preserve_root);

        try
        {
            preserve_compatible_items(options.InstallRoot, preserve_root, plan.ProtectedItems, progress);

            progress("Extracting base package.", 10);
            foreach (var archive in plan.BaseArchives)
            {
                string target = safe_combine(extract_root, archive.ExtractPath);
                Directory.CreateDirectory(target);
                ZipFile.ExtractToDirectory(archive.Path, target, overwriteFiles: true);
            }

            if (plan.PythonRequired && plan.PythonArchive is not null)
            {
                progress("Extracting embedded Python.", 20);
                string target = safe_combine(extract_root, plan.PythonArchive.ExtractPath);
                if (Directory.Exists(target))
                    Directory.Delete(target, recursive: true);
                Directory.CreateDirectory(target);
                ZipFile.ExtractToDirectory(plan.PythonArchive.Path, target, overwriteFiles: true);
            }

            progress("Removing old installation files.", 30);
            clean_installation(options.InstallRoot, options.UpdaterPath);

            progress("Copying base package.", 42);
            copy_directory(extract_root, options.InstallRoot, options.UpdaterPath, progress, 42, 25);
            restore_compatible_items(preserve_root, options.InstallRoot, plan.ProtectedItems, progress);

            var python_exe = find_python(options.InstallRoot);
            var installed_packages = read_python_packages(python_exe, progress);
            foreach (var package in plan.PythonPackages)
            {
                string? installed = installed_packages.FirstOrDefault(item => string.Equals(item.Name, package.Name, StringComparison.OrdinalIgnoreCase))?.Version;
                if (installed is not null && version_satisfies(installed, package.VersionRange))
                    continue;

                if (installed is not null)
                    run_process(python_exe, $"-m pip uninstall -y {package.Name}", progress, $"Removing {package.Name}.");

                run_process(python_exe, $"-m pip install \"{package.Name}{package.VersionRange}\"", progress, $"Installing {package.Name}.");
            }

            var final_packages = read_python_packages(python_exe, progress);
            InstalledState.Save(options.MetadataPath, new InstalledState(
                plan.TargetVersion.ToString(),
                plan.PythonRequirement is null ? [] : [plan.PythonRequirement.InstallKey],
                final_packages));
            progress("Installation complete.", 100);
        }
        finally
        {
            delete_quietly(extract_root);
            delete_quietly(preserve_root);
            delete_quietly(plan.StagingRoot);
        }
    }

    private Control build_summary_page(UpdatePlan update_plan)
    {
        return new StackPanel
        {
            Margin = new Thickness(0, 24, 0, 0),
            Spacing = 12,
            Children =
            {
                info_row("Base package", $"{update_plan.BaseArchives.Count} archive(s) staged"),
                info_row("Embedded Python", update_plan.PythonRequired ? "Will be installed or replaced" : "Installed version satisfies metadata"),
                info_row("Python packages", $"{update_plan.PythonPackages.Count(package => !package.Satisfied)} package(s) need pip changes"),
                info_row("Protected definitions", $"{update_plan.ProtectedItems.Count(item => item.Compatible)} compatible, {update_plan.ProtectedItems.Count(item => !item.Compatible)} deprecated")
            }
        };
    }

    private Control build_review_page(UpdatePlan update_plan)
    {
        var list = new StackPanel { Spacing = 8 };
        list.Children.Add(section("Installation"));
        list.Children.Add(info_row("Application", $"Replace with Gated {update_plan.TargetVersion}"));
        list.Children.Add(info_row("Python", update_plan.PythonRequired ? "Reinstall embedded Python and discard previous packages" : "Keep current embedded Python"));

        list.Children.Add(section("Macros and statistics"));
        foreach (var item in update_plan.ProtectedItems.DefaultIfEmpty(new ProtectedItem("none", "No existing macro or statistic definitions were found.", true)))
            list.Children.Add(info_row(item.Kind, item.RelativePath, item.Compatible ? brush(129, 201, 149) : brush(235, 132, 121)));

        list.Children.Add(section("Python packages"));
        foreach (var package in update_plan.PythonPackages)
        {
            string detail = package.Satisfied
                ? $"{package.Name} {package.InstalledVersion} satisfies {package.VersionRange}"
                : $"{package.Name} {package.VersionRange} will be installed";
            list.Children.Add(info_row("pip", detail, package.Satisfied ? brush(129, 201, 149) : brush(244, 196, 107)));
        }

        return new ScrollViewer
        {
            Margin = new Thickness(0, 18, 0, 0),
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = list
        };
    }

    private Control build_progress_page() =>
        new StackPanel
        {
            Margin = new Thickness(0, 32, 0, 0),
            Spacing = 12,
            Children =
            {
                text("Applying update. This window will restart Gated when finished.", 14),
                text("Do not launch Gated manually until the updater has completed.", 12)
            }
        };

    private static VersionEntry[] load_versions(UpdateOptions options)
    {
        string xml;
        if (!string.IsNullOrWhiteSpace(options.LocalVersionsPath) && File.Exists(options.LocalVersionsPath))
        {
            xml = File.ReadAllText(options.LocalVersionsPath);
        }
        else
        {
            ensure_connection(new Uri(options.VersionsUrl));
            xml = http_client.GetStringAsync(options.VersionsUrl).GetAwaiter().GetResult();
        }

        var document = XDocument.Parse(xml);
        return document.Root?.Elements("version").Select(parse_version).OrderBy(item => item.Version).ToArray() ?? [];
    }

    private static VersionEntry parse_version(XElement element)
    {
        var requirements = element.Elements("require").Select(parse_requirement).ToArray();
        return new VersionEntry(
            new AppVersion(required_int(element, "major"), required_int(element, "minor"), required_int(element, "patch")),
            (string?)element.Attribute("platform"),
            (string?)element.Attribute("minimal"),
            element.Elements("archive").Select(parse_archive).ToArray(),
            requirements,
            element.Elements("protect").Select(item => normalize_extract_path((string?)item.Attribute("extract") ?? ".")).ToArray());
    }

    private static Requirement parse_requirement(XElement element) =>
        new(
            (string?)element.Attribute("type") ?? "",
            (string?)element.Attribute("name") ?? "",
            required_int_or_default(element, "major"),
            required_int_or_default(element, "minor"),
            bool.TryParse((string?)element.Attribute("override_if_exist"), out bool override_if_exist) && override_if_exist,
            element.Attribute("href") is { } href ? new Uri(href.Value) : null,
            normalize_extract_path((string?)element.Attribute("extract") ?? "."),
            element.Elements("require")
                .Where(child => (string?)child.Attribute("type") == "python-package")
                .Select(child => new PythonRequirement((string?)child.Attribute("name") ?? "", (string?)child.Attribute("version") ?? ""))
                .Where(child => !string.IsNullOrWhiteSpace(child.Name))
                .ToArray());

    private static UpdateArchive parse_archive(XElement element) =>
        new(new Uri((string?)element.Attribute("href") ?? throw new InvalidDataException("Archive href is missing.")),
            normalize_extract_path((string?)element.Attribute("extract") ?? "."));

    private static StagedArchive[] download_archives(IReadOnlyList<UpdateArchive> archives, string staging_root, string prefix, Action<string, double?> progress)
    {
        var staged = new List<StagedArchive>();
        for (int i = 0; i < archives.Count; i++)
        {
            var archive = archives[i];
            string path = Path.Combine(staging_root, $"{prefix}-{i + 1}.zip");
            progress($"Downloading {Path.GetFileName(archive.Href.LocalPath)}.", null);
            ensure_connection(archive.Href);
            using var response = http_client.GetAsync(archive.Href, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            using var source = response.Content.ReadAsStream();
            using var target = File.Create(path);
            source.CopyTo(target);
            staged.Add(new StagedArchive(path, archive.ExtractPath));
        }

        return staged.ToArray();
    }

    private static ProtectedItem[] scan_protected(string install_root, IReadOnlyList<string> protected_paths)
    {
        var result = new List<ProtectedItem>();
        foreach (string protected_path in protected_paths)
        {
            string root = safe_combine(install_root, protected_path);
            if (!Directory.Exists(root))
                continue;

            string kind = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(install_root, file);
                bool compatible = is_readable_text(file);
                result.Add(new ProtectedItem(kind, relative, compatible));
            }
        }

        return result.ToArray();
    }

    private static void preserve_compatible_items(string install_root, string preserve_root, IReadOnlyList<ProtectedItem> items, Action<string, double?> progress)
    {
        foreach (var item in items.Where(item => item.Compatible))
        {
            string source = safe_combine(install_root, item.RelativePath);
            if (!File.Exists(source))
                continue;

            string target = safe_combine(preserve_root, item.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
            progress($"Preserving {item.RelativePath}.", null);
        }
    }

    private static void restore_compatible_items(string preserve_root, string install_root, IReadOnlyList<ProtectedItem> items, Action<string, double?> progress)
    {
        foreach (var item in items.Where(item => item.Compatible))
        {
            string source = safe_combine(preserve_root, item.RelativePath);
            if (!File.Exists(source))
                continue;

            string target = safe_combine(install_root, item.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
            progress($"Restoring {item.RelativePath}.", null);
        }
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
            foreach (string child in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories).OrderByDescending(item => item.Length))
                File.SetAttributes(child, FileAttributes.Normal);
            File.SetAttributes(directory, FileAttributes.Normal);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void copy_directory(string source_root, string target_root, string updater_path, Action<string, double?> progress, double start, double weight)
    {
        var files = Directory.EnumerateFiles(source_root, "*", SearchOption.AllDirectories).ToArray();
        for (int i = 0; i < files.Length; i++)
        {
            string source = files[i];
            string relative = Path.GetRelativePath(source_root, source);
            string target = Path.Combine(target_root, relative);
            if (is_same_path(target, updater_path))
                continue;

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
            progress($"Copying {relative}.", start + ((i + 1) * weight / Math.Max(1, files.Length)));
        }
    }

    private static string find_python(string install_root)
    {
        string[] candidates =
        [
            Path.Combine(install_root, "python", "python.exe"),
            Path.Combine(install_root, "python", "python")
        ];
        string? python = candidates.FirstOrDefault(File.Exists);
        if (python is null)
            throw new FileNotFoundException("Embedded Python was not found after installation.", candidates[0]);
        return python;
    }

    private static PythonPackageState[] read_python_packages(string python_exe, Action<string, double?> progress)
    {
        progress("Reading installed Python packages.", 76);
        string output = run_process(python_exe, "-m pip list --format=json", progress, "Reading pip packages.");
        return JsonSerializer.Deserialize<PipPackage[]>(output, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })?
            .Select(item => new PythonPackageState(item.Name ?? "", item.Version ?? ""))
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .ToArray() ?? [];
    }

    private static string run_process(string file, string arguments, Action<string, double?> progress, string message)
    {
        progress(message, null);
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = file,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(file),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException($"Failed to start {file}.");

        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{message} failed: {error}");
        return output;
    }

    private static bool version_satisfies(string version, string range)
    {
        if (string.IsNullOrWhiteSpace(range))
            return true;

        foreach (string constraint in range.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string op = constraint.StartsWith(">=", StringComparison.Ordinal) ? ">=" :
                constraint.StartsWith("<=", StringComparison.Ordinal) ? "<=" :
                constraint.StartsWith("==", StringComparison.Ordinal) ? "==" :
                constraint.StartsWith(">", StringComparison.Ordinal) ? ">" :
                constraint.StartsWith("<", StringComparison.Ordinal) ? "<" : "==";
            string required = constraint[op.Length..];
            int compare = compare_versions(version, required);
            if ((op == ">=" && compare < 0) || (op == "<=" && compare > 0) || (op == "==" && compare != 0) ||
                (op == ">" && compare <= 0) || (op == "<" && compare >= 0))
                return false;
        }

        return true;
    }

    private static int compare_versions(string left, string right)
    {
        var left_parts = left.Split('.', '-', '+').Select(parse_part).ToArray();
        var right_parts = right.Split('.', '-', '+').Select(parse_part).ToArray();
        int length = Math.Max(left_parts.Length, right_parts.Length);
        for (int i = 0; i < length; i++)
        {
            int l = i < left_parts.Length ? left_parts[i] : 0;
            int r = i < right_parts.Length ? right_parts[i] : 0;
            int compare = l.CompareTo(r);
            if (compare != 0)
                return compare;
        }

        return 0;
    }

    private static int parse_part(string value) =>
        int.TryParse(new string(value.TakeWhile(char.IsDigit).ToArray()), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;

    private static void ensure_connection(Uri uri)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, uri);
        using var response = http_client.Send(request, HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed)
        {
            using var fallback = http_client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            fallback.EnsureSuccessStatusCode();
            return;
        }

        response.EnsureSuccessStatusCode();
    }

    private static void wait_for_parent(int parent_pid, bool quick)
    {
        try
        {
            using var parent = Process.GetProcessById(parent_pid);
            if (quick)
                return;
            parent.WaitForExit();
        }
        catch
        {
        }
    }

    private static bool is_readable_text(string path)
    {
        try
        {
            using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
            char[] buffer = new char[1024];
            reader.Read(buffer, 0, buffer.Length);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void render_steps(int active)
    {
        steps.Children.Clear();
        string[] labels = ["Prepare", "Review", "Install"];
        for (int i = 0; i < labels.Length; i++)
            steps.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 4),
                Background = i == active ? brush(63, 104, 171) : brush(45, 49, 57),
                Child = text(labels[i], 12, FontWeight.SemiBold)
            });
    }

    private void report(string message, double? value) =>
        Dispatcher.UIThread.Post(() => set_busy(null, message, value));

    private void set_busy(string? new_title, string detail, double? value)
    {
        if (!string.IsNullOrWhiteSpace(new_title))
            title.Text = new_title;
        progress.IsIndeterminate = value is null;
        if (value is not null)
            progress.Value = Math.Clamp(value.Value, 0, 100);
        progress_detail.Text = detail;
    }

    private void set_ready(string new_title, string detail)
    {
        title.Text = new_title;
        progress.IsIndeterminate = false;
        progress.Value = 100;
        progress_detail.Text = detail;
    }

    private void show_failure(Exception exception)
    {
        title.Text = "Update failed";
        subtitle.Text = exception.Message;
        page_host.Content = new ScrollViewer
        {
            Content = text(exception.ToString(), 12)
        };
        progress.IsVisible = false;
        back_button.IsEnabled = false;
        cancel_button.Content = "Close";
        next_button.IsEnabled = false;
    }

    private static Control info_row(string name, string value, IBrush? accent = null) =>
        new Border
        {
            Background = brush(40, 44, 52),
            BorderBrush = brush(68, 73, 84),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 9),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("150,*"),
                Children =
                {
                    text(name, 12, FontWeight.SemiBold, accent ?? brush(215, 220, 230)),
                    text(value, 12)
                }
            }
        }.also(row => Grid.SetColumn(((Grid)row.Child!).Children[1], 1));

    private static Control section(string label) =>
        text(label, 13, FontWeight.SemiBold, brush(244, 246, 250)).also(control => control.Margin = new Thickness(0, 12, 0, 0));

    private static TextBlock text(string value, double size, FontWeight weight = default, IBrush? foreground = null) =>
        new()
        {
            Text = value,
            FontSize = size,
            FontWeight = weight == default ? FontWeight.Normal : weight,
            Foreground = foreground ?? brush(215, 220, 230),
            TextWrapping = TextWrapping.Wrap
        };

    private static SolidColorBrush brush(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));

    private static UpdateOptions parse_args(string[] args)
    {
        string app = read_option(args, "--app");
        string updater = read_option(args, "--updater");
        string parent = read_option(args, "--parent-pid");
        if (!int.TryParse(parent, NumberStyles.None, CultureInfo.InvariantCulture, out int parent_pid))
            throw new ArgumentException("Invalid parent process id.");

        string install_root = Path.GetDirectoryName(app) ?? throw new ArgumentException("Invalid application path.");
        return new UpdateOptions(
            Path.GetFullPath(app),
            Path.GetFullPath(updater),
            Path.GetFullPath(install_root),
            parent_pid,
            AppVersion.Parse(read_option(args, "--current-version", "0.0.0")),
            read_optional_version(args, "--target-version"),
            read_option(args, "--versions-url", default_versions_url),
            read_option(args, "--local-versions", ""),
            Path.Combine(install_root, "gated.update.json"));
    }

    private static AppVersion? read_optional_version(string[] args, string name)
    {
        string value = read_option(args, name, "");
        return string.IsNullOrWhiteSpace(value) ? null : AppVersion.Parse(value);
    }

    private static string read_option(string[] args, string name, string? fallback = null)
    {
        int index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            if (fallback is not null)
                return fallback;
            throw new ArgumentException($"Missing {name}.");
        }

        return args[index + 1];
    }

    private static int required_int(XElement element, string attribute) =>
        int.Parse((string?)element.Attribute(attribute) ?? throw new InvalidDataException($"Version {attribute} is missing."), CultureInfo.InvariantCulture);

    private static int required_int_or_default(XElement element, string attribute) =>
        int.TryParse((string?)element.Attribute(attribute), NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : 0;

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
            throw new InvalidDataException("Path is outside the target directory.");

        return combined;
    }

    private static bool is_same_path(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static void delete_quietly(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static void close_application()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
        else
            Environment.Exit(0);
    }
}

internal static class ObjectExtensions
{
    public static T also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}

internal sealed record UpdateOptions(
    string AppPath,
    string UpdaterPath,
    string InstallRoot,
    int ParentPid,
    AppVersion CurrentVersion,
    AppVersion? TargetVersion,
    string VersionsUrl,
    string LocalVersionsPath,
    string MetadataPath);

internal sealed record UpdatePlan(
    VersionEntry Target,
    string StagingRoot,
    IReadOnlyList<StagedArchive> BaseArchives,
    StagedArchive? PythonArchive,
    Requirement? PythonRequirement,
    bool PythonRequired,
    IReadOnlyList<ProtectedItem> ProtectedItems,
    IReadOnlyList<PythonPackagePlan> PythonPackages)
{
    public AppVersion TargetVersion => Target.Version;
}

internal sealed record VersionEntry(
    AppVersion Version,
    string? Platform,
    string? MinimalSystemVersion,
    IReadOnlyList<UpdateArchive> Archives,
    IReadOnlyList<Requirement> Requirements,
    IReadOnlyList<string> ProtectedPaths)
{
    public bool IsCompatibleWith(SystemInfo system)
    {
        if (!string.IsNullOrWhiteSpace(Platform) && !string.Equals(normalize_platform(Platform), system.Platform, StringComparison.OrdinalIgnoreCase))
            return false;

        return string.IsNullOrWhiteSpace(MinimalSystemVersion) ||
               !System.Version.TryParse(MinimalSystemVersion, out var minimal) ||
               system.OsVersion.CompareTo(minimal) >= 0;
    }

    private static string normalize_platform(string platform) =>
        platform.Trim().ToLowerInvariant() switch
        {
            "win" => "windows",
            "mac" => "macos",
            "osx" => "macos",
            "darwin" => "macos",
            _ => platform.Trim().ToLowerInvariant()
        };
}

internal sealed record Requirement(
    string Type,
    string Name,
    int Major,
    int Minor,
    bool OverrideIfExist,
    Uri? Href,
    string ExtractPath,
    IReadOnlyList<PythonRequirement> PythonPackages)
{
    public string InstallKey => $"{Type}:{Name}:{Major}.{Minor}";
}

internal sealed record PythonRequirement(string Name, string VersionRange);
internal sealed record UpdateArchive(Uri Href, string ExtractPath);
internal sealed record StagedArchive(string Path, string ExtractPath);
internal sealed record ProtectedItem(string Kind, string RelativePath, bool Compatible);
internal sealed record PythonPackagePlan(string Name, string VersionRange, string? InstalledVersion, bool Satisfied);
internal sealed record PythonPackageState(string Name, string Version);
internal sealed record PipPackage(string? Name, string? Version);

internal sealed record InstalledState(string AppVersion, IReadOnlyList<string> Requirements, IReadOnlyList<PythonPackageState> PythonPackages)
{
    public static InstalledState Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<InstalledState>(File.ReadAllText(path)) ?? Empty;
        }
        catch
        {
        }

        return Empty;
    }

    public static void Save(string path, InstalledState state)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }

    public bool HasRequirement(string requirement) => Requirements.Any(item => string.Equals(item, requirement, StringComparison.OrdinalIgnoreCase));

    private static InstalledState Empty => new("", [], []);
}

internal readonly record struct AppVersion(int Major, int Minor, int Patch) : IComparable<AppVersion>
{
    public int CompareTo(AppVersion other)
    {
        int major = Major.CompareTo(other.Major);
        if (major != 0)
            return major;
        int minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}";

    public static AppVersion Parse(string value)
    {
        var parts = value.Split('+')[0].Split('-')[0].Split('.');
        return new AppVersion(
            int.Parse(parts.ElementAtOrDefault(0) ?? "0", CultureInfo.InvariantCulture),
            int.Parse(parts.ElementAtOrDefault(1) ?? "0", CultureInfo.InvariantCulture),
            int.Parse(parts.ElementAtOrDefault(2) ?? "0", CultureInfo.InvariantCulture));
    }
}

internal sealed record SystemInfo(string Platform, Version OsVersion)
{
    public static SystemInfo Current()
    {
        string platform =
            OperatingSystem.IsWindows() ? "windows" :
            OperatingSystem.IsMacOS() ? "macos" :
            OperatingSystem.IsLinux() ? "linux" :
            "unknown";
        return new SystemInfo(platform, Environment.OSVersion.Version);
    }
}
