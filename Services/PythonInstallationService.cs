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

namespace gated.Services;

public sealed class PythonInstallationService
{
    private static readonly HttpClient http_client = new() { Timeout = TimeSpan.FromSeconds(30) };

    public bool HasLocalPythonInstallation()
    {
        if (!Directory.Exists(PlatformSupport.EmbeddedPythonHome()) ||
            !File.Exists(PlatformSupport.EmbeddedPythonLibraryPath()) ||
            !File.Exists(PlatformSupport.EmbeddedPythonExecutablePath()) ||
            !File.Exists(PlatformSupport.UpdateMetadataPath))
            return false;

        var installed = PythonInstalledState.Load(PlatformSupport.UpdateMetadataPath);
        return installed.Requirements.Count > 0 && installed.PythonPackages.Count > 0;
    }

    public async Task<bool> EnsurePythonInstallationAsync(
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellation_token = default)
    {
        progress?.Report(new UpdateProgress("Checking Python runtime ...", "Reading Python requirements.", null));
        var target = await load_target_version(cancellation_token);
        var python = target.Requirements.FirstOrDefault(requirement =>
            string.Equals(requirement.Type, "package", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(requirement.Name, "python", StringComparison.OrdinalIgnoreCase));

        if (python is null)
            throw new InvalidDataException("The version manifest does not contain an embedded Python requirement.");

        var installed = PythonInstalledState.Load(PlatformSupport.UpdateMetadataPath);
        bool python_exists = File.Exists(PlatformSupport.EmbeddedPythonLibraryPath()) &&
                             File.Exists(PlatformSupport.EmbeddedPythonExecutablePath());
        bool python_required = python.OverrideIfExist || !python_exists || !installed.HasRequirement(python.InstallKey);
        bool changed = false;

        if (python_required)
        {
#if !MSIX
            if (python.Href is null)
                throw new InvalidDataException("The Python requirement does not contain a download URL.");
#endif

            await install_python_runtime(python, progress, cancellation_token);
            changed = true;
        }

        string python_exe = find_python();
        var installed_packages = await read_python_packages(python_exe, progress, cancellation_token);
        foreach (var package in python.PythonPackages)
        {
            string? installed_version = installed_packages
                .FirstOrDefault(item => string.Equals(item.Name, package.Name, StringComparison.OrdinalIgnoreCase))
                ?.Version;
            if (installed_version is not null && version_satisfies(installed_version, package.VersionRange))
                continue;

            if (installed_version is not null)
                await run_process(
                    python_exe,
                    ["-m", "pip", "uninstall", "-y", package.Name],
                    progress,
                    $"Removing {package.Name}.",
                    cancellation_token);

            await run_process(
                python_exe,
                ["-m", "pip", "install", package.Name + package.VersionRange],
                progress,
                $"Installing {package.Name}.",
                cancellation_token);
            changed = true;
        }

        var final_packages = await read_python_packages(python_exe, progress, cancellation_token);
        PythonInstalledState.Save(PlatformSupport.UpdateMetadataPath, new PythonInstalledState(
            target.Version.ToString(),
            [python.InstallKey],
            final_packages));
        progress?.Report(new UpdateProgress("Python runtime is ready", "Embedded Python and required packages are available.", 1));
        return changed;
    }

    private static async Task<PythonVersionEntry> load_target_version(CancellationToken cancellation_token)
    {
#if MSIX
        await Task.CompletedTask;
        return new PythonVersionEntry(
            new AppVersion(3, 13, 0),
            PlatformSupport.CurrentPlatform,
            PlatformSupport.CurrentArchitecture,
            null,
            [new PythonRuntimeRequirement(
                "package", "python", 3, 13, false, null, ".",
                PlatformSupport.RequiredPythonPackages
                    .Select(package => new PythonPackageRequirement(package.Name, package.VersionCondition))
                    .ToArray())
            ]);
#else
        await ensure_connection_async(new Uri(UpdateManager.VersionsUrl), cancellation_token);
        using var response = await http_client.GetAsync(UpdateManager.VersionsUrl, cancellation_token);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellation_token);
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellation_token);
        var system = UpdateManager.GetCurrentSystemInfo();
        return document.Root?.Elements("version")
            .Select(parse_version)
            .Where(version => version.IsCompatibleWith(system))
            .OrderBy(version => version.Version)
            .LastOrDefault()
            ?? throw new InvalidDataException("No compatible Python runtime metadata was found.");
#endif
    }

    private static PythonVersionEntry parse_version(XElement element) =>
        new(
            new AppVersion(required_int(element, "major"), required_int(element, "minor"), required_int(element, "patch")),
            (string?)element.Attribute("platform"),
            (string?)element.Attribute("arch"),
            (string?)element.Attribute("minimal"),
            element.Elements("require").Select(parse_requirement).ToArray());

    private static PythonRuntimeRequirement parse_requirement(XElement element) =>
        new(
            (string?)element.Attribute("type") ?? "",
            (string?)element.Attribute("name") ?? "",
            required_int_or_default(element, "major"),
            required_int_or_default(element, "minor"),
            bool.TryParse((string?)element.Attribute("override_if_exist"), out bool override_if_exist) && override_if_exist,
            element.Attribute("href") is { } href ? new Uri(href.Value) : null,
            normalize_extract_path((string?)element.Attribute("extract") ?? "."),
            element.Elements("require")
                .Where(child => string.Equals((string?)child.Attribute("type"), "python-package", StringComparison.OrdinalIgnoreCase))
                .Select(child => new PythonPackageRequirement((string?)child.Attribute("name") ?? "", (string?)child.Attribute("version") ?? ""))
                .Where(child => !string.IsNullOrWhiteSpace(child.Name))
                .ToArray());

    private static async Task install_python_runtime(
        PythonRuntimeRequirement python,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellation_token)
    {
#if MSIX
        await install_embedded_python_runtime(progress, cancellation_token);
#else
        string staging_root = Path.Combine(Path.GetTempPath(), "gated-python-stage-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        string extract_root = Path.Combine(Path.GetTempPath(), "gated-python-extract-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        string archive_path = Path.Combine(staging_root, "python.zip");
        Directory.CreateDirectory(staging_root);
        Directory.CreateDirectory(extract_root);

        try
        {
            await download_archive(python.Href!, archive_path, progress, cancellation_token);
            progress?.Report(new UpdateProgress("Installing Python runtime ...", "Extracting embedded Python.", null));
            string extract_path = safe_combine(extract_root, python.ExtractPath);
            Directory.CreateDirectory(extract_path);
            ZipFile.ExtractToDirectory(archive_path, extract_path, overwriteFiles: true);

            string python_root = PlatformSupport.EmbeddedPythonHome();
            progress?.Report(new UpdateProgress("Installing Python runtime ...", "Replacing embedded Python files.", null));
            if (Directory.Exists(python_root))
                delete_directory_with_retries(python_root);
            copy_directory(extract_root, PlatformSupport.PersistenceDirectory, progress, 0.45, 0.25);
            ensure_executable_bit(PlatformSupport.EmbeddedPythonExecutablePath());
        }
        finally
        {
            delete_quietly(staging_root);
            delete_quietly(extract_root);
        }
#endif
    }

#if MSIX
    private static async Task install_embedded_python_runtime(
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellation_token)
    {
        string archive_path = Path.Combine(AppContext.BaseDirectory, "python-313.zip");
        if (!File.Exists(archive_path))
            throw new FileNotFoundException("Python is not embedded in the MSIX package.", archive_path);

        await Task.Run(() =>
        {
            string extract_root = Path.Combine(Path.GetTempPath(), "gated-python-msix-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(extract_root);

            try
            {
                extract_archive_with_progress(archive_path, extract_root, progress, cancellation_token);
                string source_root = embedded_python_payload_root(extract_root);
                string python_root = PlatformSupport.EmbeddedPythonHome();
                progress?.Report(new UpdateProgress("Installing Python runtime ...", "Replacing embedded Python files.", null));
                if (Directory.Exists(python_root))
                    delete_directory_with_retries(python_root);
                Directory.CreateDirectory(Path.GetDirectoryName(python_root)!);
                copy_directory(source_root, python_root, progress, 0.75, 0.20);
                ensure_executable_bit(PlatformSupport.EmbeddedPythonExecutablePath());
            }
            finally
            {
                delete_quietly(extract_root);
            }
        }, cancellation_token);
    }

    private static string embedded_python_payload_root(string extract_root)
    {
        if (File.Exists(Path.Combine(extract_root, OperatingSystem.IsWindows() ? "python.exe" : Path.Combine("bin", "python3"))))
            return extract_root;

        string python_child = Path.Combine(extract_root, "python");
        if (Directory.Exists(python_child) &&
            File.Exists(Path.Combine(python_child, OperatingSystem.IsWindows() ? "python.exe" : Path.Combine("bin", "python3"))))
            return python_child;

        return extract_root;
    }
#endif

    private static void extract_archive_with_progress(
        string archive_path,
        string target_root,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellation_token)
    {
        using var archive = ZipFile.OpenRead(archive_path);
        if (archive.Entries.Count == 0)
            throw new InvalidDataException("Embedded Python archive is empty.");

        long total = archive.Entries.Sum(entry => Math.Max(0, entry.Length));
        long extracted = 0;
        progress?.Report(new UpdateProgress("Installing Python runtime ...", "Decompressing payload ...", 0));
        foreach (var entry in archive.Entries)
        {
            cancellation_token.ThrowIfCancellationRequested();
            string target_path = safe_combine(target_root, entry.FullName);
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target_path);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target_path)!);
            using var source = entry.Open();
            using var target = File.Create(target_path);
            var buffer = new byte[1024 * 64];
            while (true)
            {
                cancellation_token.ThrowIfCancellationRequested();
                int read = source.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    break;

                target.Write(buffer, 0, read);
                extracted += read;
                double? fraction = total > 0 ? Math.Min(0.75, (double)extracted / total * 0.75) : null;
                progress?.Report(new UpdateProgress("Installing Python runtime ...", "Decompressing payload ...", fraction));
            }
        }
    }

    private static async Task download_archive(
        Uri uri,
        string target_path,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellation_token)
    {
        progress?.Report(new UpdateProgress("Downloading Python runtime ...", Path.GetFileName(uri.LocalPath), 0));
        await ensure_connection_async(uri, cancellation_token);
        using var response = await http_client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation_token);
        response.EnsureSuccessStatusCode();

        long? length = response.Content.Headers.ContentLength;
        await using (var source = await response.Content.ReadAsStreamAsync(cancellation_token))
        await using (var target = File.Create(target_path))
        {
            var buffer = new byte[1024 * 64];
            long total_read = 0;
            while (true)
            {
                int read = await source.ReadAsync(buffer, cancellation_token);
                if (read == 0)
                    break;

                await target.WriteAsync(buffer.AsMemory(0, read), cancellation_token);
                total_read += read;
                double? fraction = length > 0 ? (double)total_read / length.Value * 0.45 : null;
                progress?.Report(new UpdateProgress(
                    "Downloading Python runtime ...",
                    $"{Path.GetFileName(uri.LocalPath)} ({format_bytes(total_read)}{(length > 0 ? " / " + format_bytes(length.Value) : "")})",
                    fraction));
            }
        }

        using var zip = ZipFile.OpenRead(target_path);
        if (zip.Entries.Count == 0)
            throw new InvalidDataException("Downloaded Python archive is empty.");
    }

    private static string find_python()
    {
        string python = PlatformSupport.EmbeddedPythonExecutablePath();
        if (!File.Exists(python) && OperatingSystem.IsWindows())
            python = Path.Combine(PlatformSupport.EmbeddedPythonHome(), "python");
        if (!File.Exists(python))
            throw new FileNotFoundException("Embedded Python was not found after installation.", PlatformSupport.EmbeddedPythonExecutablePath());
        return python;
    }

    private static async Task<IReadOnlyList<PythonPackageState>> read_python_packages(
        string python_exe,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellation_token)
    {
        string output = await run_process(
            python_exe,
            ["-m", "pip", "list", "--format=json"],
            progress,
            "Reading installed Python packages.",
            cancellation_token);
        return JsonSerializer.Deserialize<PipPackage[]>(output, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })?
            .Select(item => new PythonPackageState(item.Name ?? "", item.Version ?? ""))
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .ToArray() ?? [];
    }

    private static async Task<string> run_process(
        string file,
        IReadOnlyList<string> arguments,
        IProgress<UpdateProgress>? progress,
        string message,
        CancellationToken cancellation_token)
    {
        progress?.Report(new UpdateProgress("Installing Python packages ...", message, null));
        var start_info = new ProcessStartInfo
        {
            FileName = file,
            WorkingDirectory = Path.GetDirectoryName(file),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            start_info.ArgumentList.Add(argument);

        using var process = Process.Start(start_info) ?? throw new InvalidOperationException($"Failed to start {file}.");
        await process.StandardInput.DisposeAsync();
        string output = await process.StandardOutput.ReadToEndAsync(cancellation_token);
        string error = await process.StandardError.ReadToEndAsync(cancellation_token);
        await process.WaitForExitAsync(cancellation_token);
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

    private static async Task ensure_connection_async(Uri uri, CancellationToken cancellation_token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, uri);
        using var response = await http_client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation_token);
        if (response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed)
        {
            using var fallback = await http_client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation_token);
            fallback.EnsureSuccessStatusCode();
            return;
        }

        response.EnsureSuccessStatusCode();
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

        if (!combined.Equals(Path.GetFullPath(root), path_comparison()) &&
            !combined.StartsWith(full_root, path_comparison()))
            throw new InvalidDataException("Path is outside the target directory.");

        return combined;
    }

    private static void copy_directory(
        string source_root,
        string target_root,
        IProgress<UpdateProgress>? progress,
        double start,
        double weight)
    {
        var files = Directory.EnumerateFiles(source_root, "*", SearchOption.AllDirectories).ToArray();
        for (int i = 0; i < files.Length; i++)
        {
            string source = files[i];
            string relative = Path.GetRelativePath(source_root, source);
            string target = Path.Combine(target_root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
                File.SetAttributes(target, FileAttributes.Normal);
            File.Copy(source, target, overwrite: true);
            progress?.Report(new UpdateProgress(
                "Installing Python runtime ...",
                $"Copying {relative}.",
                start + ((i + 1) * weight / Math.Max(1, files.Length))));
        }
    }

    private static void delete_directory_with_retries(string path)
    {
        retry_io(() =>
        {
            if (!Directory.Exists(path))
                return;

            foreach (string child in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories).OrderByDescending(item => item.Length))
                set_normal_attributes(child);
            set_normal_attributes(path);
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

    private static void ensure_executable_bit(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
            return;

        try
        {
            var mode = File.GetUnixFileMode(path);
            mode |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(path, mode);
        }
        catch
        {
        }
    }

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

    private static StringComparison path_comparison() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}

internal sealed record PythonVersionEntry(
    AppVersion Version,
    string? Platform,
    string? Arch,
    string? MinimalSystemVersion,
    IReadOnlyList<PythonRuntimeRequirement> Requirements)
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

internal sealed record PythonRuntimeRequirement(
    string Type,
    string Name,
    int Major,
    int Minor,
    bool OverrideIfExist,
    Uri? Href,
    string ExtractPath,
    IReadOnlyList<PythonPackageRequirement> PythonPackages)
{
    public string InstallKey => $"{Type}:{Name}:{Major}.{Minor}";
}

internal sealed record PythonPackageRequirement(string Name, string VersionRange);
internal sealed record PythonPackageState(string Name, string Version);
internal sealed record PipPackage(string? Name, string? Version);

internal sealed record PythonInstalledState(string AppVersion, IReadOnlyList<string> Requirements, IReadOnlyList<PythonPackageState> PythonPackages)
{
    public static PythonInstalledState Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<PythonInstalledState>(File.ReadAllText(path)) ?? Empty;
        }
        catch
        {
        }

        return Empty;
    }

    public static void Save(string path, PythonInstalledState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }

    public bool HasRequirement(string requirement) => Requirements.Any(item => string.Equals(item, requirement, StringComparison.OrdinalIgnoreCase));

    private static PythonInstalledState Empty => new("", [], []);
}
