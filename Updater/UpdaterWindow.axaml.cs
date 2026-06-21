using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
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
using gated.Shared;

namespace gated.Updater;

public sealed partial class UpdaterWindow : Window
{
    private const string default_versions_url = "https://raw.githubusercontent.com/yang-z-03/gated/refs/heads/master/.github/versions";
    private static readonly HttpClient http_client = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly string[] args;
    private readonly TextBlock title;
    private readonly TextBlock subtitle;
    private readonly Border[] step_indicators;
    private readonly Control summary_page;
    private readonly Control review_page;
    private readonly Control install_page;
    private readonly Control failure_page;
    private readonly StackPanel review_content;
    private readonly TextBlock summary_base_package;
    private readonly TextBlock summary_python;
    private readonly TextBlock summary_python_packages;
    private readonly TextBlock summary_scripts;
    private readonly TextBlock failure_primary;
    private readonly TextBlock failure_secondary;
    private readonly Button back_button;
    private readonly Button cancel_button;
    private readonly Button next_button;
    private readonly ProgressBar progress;
    private readonly TextBlock progress_detail;

    private UpdateOptions? options;
    private UpdatePlan? plan;
    private int page_index;

    public UpdaterWindow() : this([])
    {
    }

    public UpdaterWindow(string[] args)
    {
        this.args = args;
        AvaloniaXamlLoader.Load(this);
        title = require_control<TextBlock>("TitleText");
        subtitle = require_control<TextBlock>("SubtitleText");
        step_indicators =
        [
            require_control<Border>("StepPrepare"),
            require_control<Border>("StepReview"),
            require_control<Border>("StepInstall")
        ];
        summary_page = require_control<Control>("SummaryPage");
        review_page = require_control<Control>("ReviewPage");
        install_page = require_control<Control>("InstallPage");
        failure_page = require_control<Control>("FailurePage");
        review_content = require_control<StackPanel>("ReviewContent");
        summary_base_package = require_control<TextBlock>("SummaryBasePackage");
        summary_python = require_control<TextBlock>("SummaryPython");
        summary_python_packages = require_control<TextBlock>("SummaryPythonPackages");
        summary_scripts = require_control<TextBlock>("SummaryScripts");
        failure_primary = require_control<TextBlock>("FailurePrimary");
        failure_secondary = require_control<TextBlock>("FailureSecondary");
        back_button = require_control<Button>("BackButton");
        cancel_button = require_control<Button>("CancelButton");
        next_button = require_control<Button>("NextButton");
        progress = require_control<ProgressBar>("Progress");
        progress_detail = require_control<TextBlock>("ProgressDetail");

        back_button.Click += (_, _) => show_page(Math.Max(0, page_index - 1));
        cancel_button.Click += (_, _) => close_application();
        next_button.Click += async (_, _) => await advance();
        Opened += async (_, _) => await initialize();
    }

    private T require_control<T>(string name) where T : Control =>
        this.FindControl<T>(name) ?? throw new InvalidOperationException($"UpdaterWindow control '{name}' was not found.");

    private async Task initialize()
    {
        try
        {
            options = parse_args(args);
            set_busy("Checking metadata", "Loading version manifest.", null);
            plan = await Task.Run(() => build_plan(options, report));
            if (!options.RequirementsOnly && plan.TargetVersion.CompareTo(options.CurrentVersion) <= 0 && options.TargetVersion is null)
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
            title.Text = options?.RequirementsOnly == true ? "Python runtime is ready" : $"Gated {plan.TargetVersion} is ready";
            subtitle.Text = "Required archives have been staged. Review the planned installation before applying it.";
            populate_summary_page(plan);
            show_only(summary_page);
            set_ready("Downloads ready", "All required update archives are available locally.");
        }
        else if (index == 1)
        {
            title.Text = "Review changes";
            subtitle.Text = "Confirm package, Python, macro, statistic, and pip changes.";
            populate_review_page(plan);
            show_only(review_page);
            set_ready("Waiting for confirmation", "No installation files have been replaced yet.");
        }
        else
        {
            title.Text = "Installing update";
            subtitle.Text = options?.RequirementsOnly == true
                ? "Installing embedded Python and reconciling Python packages."
                : "Replacing application files and reconciling Python packages.";
            show_only(install_page);
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

        var staged_archives = options.RequirementsOnly
            ? []
            : download_archives(target.Archives, staging_root, "base", progress);
        var python = target.Requirements.FirstOrDefault(requirement => requirement.Type == "package" && requirement.Name == "python");
        bool python_exists = File.Exists(PlatformSupport.EmbeddedPythonLibraryPath(options.InstallRoot));
        bool python_required = python is not null &&
            (python.OverrideIfExist || !python_exists || (!options.RequirementsOnly && !installed.HasRequirement(python.InstallKey)));
        StagedArchive? staged_python = null;
        if (python_required && python is not null && python.Href is not null)
            staged_python = download_archives([new UpdateArchive(python.Href, python.ExtractPath)], staging_root, "python", progress).Single();
        if (options.RequirementsOnly && python is null)
            throw new InvalidDataException("The version manifest does not contain an embedded Python requirement.");

        var protected_items = scan_scripts(options.InstallRoot, target.ScriptAreas);
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
        Thread.Sleep(750);
        terminate_embedded_python_processes(options.InstallRoot);
        string extract_root = Path.Combine(Path.GetTempPath(), "gated-update-extract-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        string preserve_root = Path.Combine(Path.GetTempPath(), "gated-update-preserve-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(extract_root);
        Directory.CreateDirectory(preserve_root);

        try
        {
            progress("Checking installation file access.", 4);
            if (options.RequirementsOnly)
                assert_python_can_be_replaced_if_needed(options.InstallRoot, plan.PythonRequired);
            else
                assert_installation_can_be_replaced(options.InstallRoot, options.UpdaterPath);

            if (!options.RequirementsOnly)
            {
                preserve_compatible_items(options.InstallRoot, preserve_root, plan.ProtectedItems, progress);

                progress("Extracting base package.", 10);
                foreach (var archive in plan.BaseArchives)
                {
                    string target = safe_combine(extract_root, archive.ExtractPath);
                    Directory.CreateDirectory(target);
                    ZipFile.ExtractToDirectory(archive.Path, target, overwriteFiles: true);
                }
            }

            if (plan.PythonRequired && plan.PythonArchive is not null)
            {
                progress("Extracting embedded Python.", 20);
                string target = safe_combine(extract_root, plan.PythonArchive.ExtractPath);
                if (Directory.Exists(target))
                    delete_directory_with_retries(target, progress);
                Directory.CreateDirectory(target);
                ZipFile.ExtractToDirectory(plan.PythonArchive.Path, target, overwriteFiles: true);
            }

            if (options.RequirementsOnly)
            {
                if (plan.PythonRequired)
                {
                    string python_root = Path.Combine(options.InstallRoot, "python");
                    progress("Installing embedded Python.", 30);
                    if (Directory.Exists(python_root))
                        delete_directory_with_retries(python_root, progress);
                    copy_directory(extract_root, options.InstallRoot, options.UpdaterPath, progress, 35, 25);
                }
            }
            else
            {
                progress("Removing old installation files.", 30);
                clean_installation(
                    options.InstallRoot,
                    options.UpdaterPath,
                    plan.PythonRequired ? [] : ["python"]);

                progress("Copying base package.", 42);
                copy_directory(extract_root, options.InstallRoot, options.UpdaterPath, progress, 42, 25);
                restore_compatible_items(preserve_root, options.InstallRoot, plan.ProtectedItems, progress);
            }

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

    private void populate_summary_page(UpdatePlan update_plan)
    {
        summary_base_package.Text = options?.RequirementsOnly == true ? "Main program will not be updated" : $"{update_plan.BaseArchives.Count} archive(s) staged";
        summary_python.Text = update_plan.PythonRequired ? "Will be installed or replaced" : "Installed version satisfies metadata";
        summary_python_packages.Text = $"{update_plan.PythonPackages.Count(package => !package.Satisfied)} package(s) need pip changes";
        summary_scripts.Text = $"{update_plan.ProtectedItems.Count(item => item.Compatible)} kept, {update_plan.ProtectedItems.Count(item => !item.Compatible)} removed";
    }

    private void populate_review_page(UpdatePlan update_plan)
    {
        review_content.Children.Clear();
        review_content.Children.Add(section("Package"));
        review_content.Children.Add(table(
            ["Item", "Current", "Requested", "Action"],
            [[
                "Gated",
                options?.CurrentVersion.ToString() ?? "unknown",
                update_plan.TargetVersion.ToString(),
                options?.RequirementsOnly == true ? "Keep" : "Update"
            ]],
            [1, 2]));

        review_content.Children.Add(section("Embedded Python"));
        string python_version = update_plan.PythonRequirement is null
            ? "not requested"
            : $"{update_plan.PythonRequirement.Major}.{update_plan.PythonRequirement.Minor}";
        review_content.Children.Add(table(
            ["Requirement", "Installed", "Requested", "Action"],
            [["python", update_plan.PythonRequired ? "missing or forced" : "installed", python_version, update_plan.PythonRequired ? "Fresh install" : "Keep"]],
            [2]));

        review_content.Children.Add(section("Python packages"));
        var package_rows = update_plan.PythonPackages.Count == 0
            ? [["none", "", "", "No package requirements"]]
            : update_plan.PythonPackages
                .Select(package => new[]
                {
                    package.Name,
                    string.IsNullOrWhiteSpace(package.InstalledVersion) ? "not installed" : package.InstalledVersion!,
                    package.VersionRange,
                    package.Satisfied ? "Keep" : "Install"
                })
                .ToArray();
        review_content.Children.Add(table(["Package", "Installed", "Requested", "Action"], package_rows, [1, 2]));

        review_content.Children.Add(section("Macros and statistics"));
        var script_rows = update_plan.ProtectedItems.Count == 0
            ? [["none", "", "", "", "No existing macro or statistic definitions"]]
            : update_plan.ProtectedItems
                .OrderBy(item => item.Kind, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item => new[]
                {
                    item.Kind,
                    item.Name,
                    item.ApiVersion?.ToString(CultureInfo.InvariantCulture) ?? "invalid",
                    item.RequiredApiVersion.ToString(CultureInfo.InvariantCulture),
                    item.Compatible ? "Keep" : "Remove"
                })
                .ToArray();
        review_content.Children.Add(table(["Type", "Name", "API", "Required", "Action"], script_rows, [2, 3]));
        review_content.Children.Add(new Grid() { Height = 20 });
    }

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
        var script_areas = element.Elements()
            .Where(child => child.Name.LocalName is "macros" or "statistics")
            .Select(child => new ScriptArea(
                child.Name.LocalName,
                normalize_extract_path((string?)child.Attribute("extract") ?? "."),
                required_int_or_default(child, "compat")))
            .Concat(element.Elements("protect").Select(item => new ScriptArea(
                Path.GetFileName(normalize_extract_path((string?)item.Attribute("extract") ?? ".")).ToLowerInvariant(),
                normalize_extract_path((string?)item.Attribute("extract") ?? "."),
                0)))
            .ToArray();
        return new VersionEntry(
            new AppVersion(required_int(element, "major"), required_int(element, "minor"), required_int(element, "patch")),
            (string?)element.Attribute("platform"),
            (string?)element.Attribute("arch"),
            (string?)element.Attribute("minimal"),
            element.Elements("archive").Select(parse_archive).ToArray(),
            requirements,
            script_areas);
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
            copy_stream_with_progress(source, target, response.Content.Headers.ContentLength, Path.GetFileName(archive.Href.LocalPath), progress);
            staged.Add(new StagedArchive(path, archive.ExtractPath));
        }

        return staged.ToArray();
    }

    private static void copy_stream_with_progress(Stream source, Stream target, long? length, string file_name, Action<string, double?> progress)
    {
        var buffer = new byte[1024 * 64];
        long total_read = 0;
        while (true)
        {
            int read = source.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;

            target.Write(buffer, 0, read);
            total_read += read;
            double? fraction = length > 0 ? (double)total_read / length.Value * 100.0 : null;
            string detail = length > 0
                ? $"Downloading {file_name}: {format_bytes(total_read)} / {format_bytes(length.Value)}"
                : $"Downloading {file_name}: {format_bytes(total_read)}";
            progress(detail, fraction);
        }
    }

    private static ProtectedItem[] scan_scripts(string install_root, IReadOnlyList<ScriptArea> script_areas)
    {
        var result = new List<ProtectedItem>();
        foreach (var area in script_areas)
        {
            string root = safe_combine(install_root, area.ExtractPath);
            if (!Directory.Exists(root))
                continue;

            string pattern = area.Kind == "macros" ? "*.macro" : area.Kind == "statistics" ? "*.statistic" : "*";
            foreach (string file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
            {
                var script = read_script_definition(file, area.Kind);
                string relative = Path.GetRelativePath(install_root, file);
                bool compatible = script is not null && script.ApiVersion >= area.RequiredApiVersion;
                result.Add(new ProtectedItem(
                    area.Kind,
                    script?.Name ?? Path.GetFileNameWithoutExtension(file),
                    relative,
                    script?.ApiVersion,
                    area.RequiredApiVersion,
                    compatible));
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

    private static ScriptDefinition? read_script_definition(string path, string area_kind)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (reader.ReadString() != "GATEDSCRIPT")
                return null;

            int format_version = reader.ReadInt32();
            if (format_version > 1)
                return null;

            int expected_kind = area_kind == "macros" ? 0 : 1;
            int kind = reader.ReadInt32();
            if (kind != expected_kind)
                return null;

            _ = new Guid(reader.ReadBytes(16));
            string name = reader.ReadString();
            int api_version = reader.ReadInt32();
            return new ScriptDefinition(name, api_version);
        }
        catch
        {
            return null;
        }
    }

    private static void clean_installation(string install_root, string updater_path, string[] preserved_directories)
    {
        foreach (string file in Directory.EnumerateFiles(install_root))
        {
            if (is_same_path(file, updater_path))
                continue;
            delete_file_with_retries(file);
        }

        foreach (string directory in Directory.EnumerateDirectories(install_root))
        {
            if (preserved_directories.Contains(Path.GetFileName(directory)))
                continue;

            delete_directory_with_retries(directory, null);
        }
    }

    private static void assert_installation_can_be_replaced(string install_root, string updater_path)
    {
        var occupied = find_processes_running_from_installation(install_root, updater_path).ToArray();
        if (occupied.Length > 0)
        {
            string processes = string.Join(", ", occupied.Select(process => $"{process.Name} ({process.Id})"));
            throw new IOException($"The update cannot continue because the installation is still used by another program: {processes}. Close that program and retry the update.");
        }

        foreach (string file in Directory.EnumerateFiles(install_root, "*", SearchOption.AllDirectories))
        {
            if (is_same_path(file, updater_path))
                continue;

            try
            {
                using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"The update cannot continue because '{file}' is occupied by another program. Close that program and retry the update.", exception);
            }
        }
    }

    private static void assert_python_can_be_replaced_if_needed(string install_root, bool python_required)
    {
        if (!python_required)
            return;

        string python_root = Path.Combine(install_root, "python");
        if (!Directory.Exists(python_root))
            return;

        foreach (string file in Directory.EnumerateFiles(python_root, "*", SearchOption.AllDirectories))
        {
            try
            {
                using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"The Python runtime cannot be replaced because '{file}' is occupied by another program. Close that program and retry.", exception);
            }
        }
    }

    private static IEnumerable<ProcessUse> find_processes_running_from_installation(string install_root, string updater_path)
    {
        if (!OperatingSystem.IsWindows())
            yield break;

        string full_root = Path.GetFullPath(install_root);
        string current_process_path = Environment.ProcessPath ?? updater_path;
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == Environment.ProcessId)
                    continue;

                string? path;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(path) ||
                    is_same_path(path, updater_path) ||
                    is_same_path(path, current_process_path) ||
                    !is_path_under_or_same(path, full_root))
                    continue;

                yield return new ProcessUse(process.Id, process.ProcessName);
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static void terminate_embedded_python_processes(string install_root)
    {
        string python_root = Path.GetFullPath(Path.Combine(install_root, "python"));
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                string name = process.ProcessName;
                if (!name.Equals("python", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("pythonw", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("python3", StringComparison.OrdinalIgnoreCase))
                    continue;

                string? path;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(path) || !is_path_under(path, python_root))
                    continue;

                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static void delete_file_with_retries(string path)
    {
        retry_io(() =>
        {
            if (!File.Exists(path))
                return;

            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }, $"Unable to remove {path}.");
    }

    private static void delete_directory_with_retries(string path, Action<string, double?>? progress)
    {
        retry_io(() =>
        {
            if (!Directory.Exists(path))
                return;

            foreach (string child in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories).OrderByDescending(item => item.Length))
                set_normal_attributes(child);
            set_normal_attributes(path);
            progress?.Invoke($"Removing {Path.GetFileName(path)}.", null);
            Directory.Delete(path, recursive: true);
        }, $"Unable to remove {path}.");
    }

    private static void retry_io(Action action, string failure_message)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                last = exception;
                Thread.Sleep(250 + attempt * 350);
            }
        }

        throw new IOException(failure_message, last);
    }

    private static void set_normal_attributes(string path)
    {
        if (File.Exists(path))
            File.SetAttributes(path, FileAttributes.Normal);
        else if (Directory.Exists(path))
            File.SetAttributes(path, FileAttributes.Normal);
    }

    private static bool is_path_under(string path, string root)
    {
        string full_path = Path.GetFullPath(path);
        string full_root = Path.GetFullPath(root);
        if (!full_root.EndsWith(Path.DirectorySeparatorChar))
            full_root += Path.DirectorySeparatorChar;

        return full_path.StartsWith(full_root, path_comparison());
    }

    private static bool is_path_under_or_same(string path, string root)
    {
        string full_path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        string full_root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        return string.Equals(full_path, full_root, path_comparison()) || is_path_under(path, root);
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
        string python = PlatformSupport.EmbeddedPythonExecutablePath(install_root);
        if (!File.Exists(python) && OperatingSystem.IsWindows())
            python = Path.Combine(install_root, "python", "python");
        if (!File.Exists(python))
            throw new FileNotFoundException("Embedded Python was not found after installation.", PlatformSupport.EmbeddedPythonExecutablePath(install_root));
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

    private void render_steps(int active)
    {
        for (int i = 0; i < step_indicators.Length; i++)
            step_indicators[i].Background = i == active ? brush(63, 104, 171) : brush(45, 49, 57);
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
        if (exception is ArgumentException)
        {
            failure_primary.Text = "This is an updater utility and should only be called via the program itself.";
            failure_secondary.Text = "Never run it manually.";
        }
        else
        {
            failure_primary.Text = "Exception occurs when running installation task. This is an internal error, report to the developers for future fix. You can file an issue on the project page: https://github.com/yang-z-03/gated/issues and mark 'Installer failure' tag for maintainer to capture the error.";
            failure_secondary.Text = "It is unfortunate that your previous installation may have been broken by the partial installation. Visiting https://github.com/yang-z-03/gated/releases and download a copy yourself in replace, that you can proceed using the software.";
        }
        show_only(failure_page);
        progress.IsVisible = false;
        back_button.IsEnabled = false;
        cancel_button.Content = "Close";
        cancel_button.IsEnabled = true;
        next_button.IsEnabled = false;
    }

    private void show_only(Control page)
    {
        summary_page.IsVisible = ReferenceEquals(page, summary_page);
        review_page.IsVisible = ReferenceEquals(page, review_page);
        install_page.IsVisible = ReferenceEquals(page, install_page);
        failure_page.IsVisible = ReferenceEquals(page, failure_page);
    }

    private static Control table(string[] headers, IReadOnlyList<string[]> rows, int[]? code_columns = null)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(string.Join(",", headers.Select(_ => "Auto"))),
            RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", rows.Count + 1))),
            Margin = new Thickness(0, 4, 0, 0)
        };
        Grid.SetIsSharedSizeScope(grid, true);

        for (int column = 0; column < headers.Length; column++)
        {
            grid.ColumnDefinitions[column].SharedSizeGroup = $"ReviewColumn{column}";
            var header = text(headers[column], 12, FontWeight.SemiBold, brush(244, 246, 250));
            header.Margin = new Thickness(0, 0, 18, 7);
            Grid.SetColumn(header, column);
            Grid.SetRow(header, 0);
            grid.Children.Add(header);
        }

        for (int row = 0; row < rows.Count; row++)
        {
            for (int column = 0; column < headers.Length; column++)
            {
                string value = column < rows[row].Length ? rows[row][column] : "";
                var cell = text(value, 12, foreground: action_brush(value));
                if (code_columns?.Contains(column) == true)
                    cell.FontFamily = code_font_family();
                cell.Margin = new Thickness(0, 3, 18, 3);
                Grid.SetColumn(cell, column);
                Grid.SetRow(cell, row + 1);
                grid.Children.Add(cell);
            }
        }

        return new Border
        {
            Background = brush(44, 48, 57),
            BorderBrush = brush(78, 84, 96),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10),
            Child = grid
        };
    }

    private static IBrush? action_brush(string value) =>
        value is "Keep" ? brush(129, 201, 149) :
        value is "Remove" ? brush(235, 132, 121) :
        value is "Install" or "Update" or "Fresh install" ? brush(244, 196, 107) :
        null;

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

    private static FontFamily code_font_family() => new("IBM Plex Mono, Jetbrains Mono, Consolas");

    private static string format_bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

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
            Path.Combine(install_root, "gated.update.json"),
            has_flag(args, "--requirements-only"));
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

    private static bool has_flag(string[] args, string name) =>
        args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));

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

        if (!combined.Equals(Path.GetFullPath(root), path_comparison()) &&
            !combined.StartsWith(full_root, path_comparison()))
            throw new InvalidDataException("Path is outside the target directory.");

        return combined;
    }

    private static bool is_same_path(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), path_comparison());

    private static StringComparison path_comparison() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

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
    string MetadataPath,
    bool RequirementsOnly);

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
    string? Arch,
    string? MinimalSystemVersion,
    IReadOnlyList<UpdateArchive> Archives,
    IReadOnlyList<Requirement> Requirements,
    IReadOnlyList<ScriptArea> ScriptAreas)
{
    public bool IsCompatibleWith(SystemInfo system)
    {
        if (!string.IsNullOrWhiteSpace(Platform) &&
            !string.Equals(PlatformSupport.NormalizePlatform(Platform), system.Platform, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(Arch) &&
            !string.Equals(PlatformSupport.NormalizeArchitecture(Arch), system.Architecture, StringComparison.OrdinalIgnoreCase))
            return false;

        return string.IsNullOrWhiteSpace(MinimalSystemVersion) ||
               !System.Version.TryParse(MinimalSystemVersion, out var minimal) ||
               system.OsVersion.CompareTo(minimal) >= 0;
    }
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
internal sealed record ScriptArea(string Kind, string ExtractPath, int RequiredApiVersion);
internal sealed record ScriptDefinition(string Name, int ApiVersion);
internal sealed record ProtectedItem(string Kind, string Name, string RelativePath, int? ApiVersion, int RequiredApiVersion, bool Compatible);
internal sealed record PythonPackagePlan(string Name, string VersionRange, string? InstalledVersion, bool Satisfied);
internal sealed record PythonPackageState(string Name, string Version);
internal sealed record PipPackage(string? Name, string? Version);
internal sealed record ProcessUse(int Id, string Name);

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

internal sealed record SystemInfo(string Platform, string Architecture, Version OsVersion)
{
    public static SystemInfo Current()
    {
        return new SystemInfo(
            PlatformSupport.CurrentPlatform,
            PlatformSupport.CurrentArchitecture,
            Environment.OSVersion.Version);
    }
}
