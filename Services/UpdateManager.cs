using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using gated.Shared;

namespace gated.Services;

public sealed class UpdateManager
{
    public const string VersionsUrl = "https://raw.githubusercontent.com/yang-z-03/gated/refs/heads/master/.github/versions";
    public const string UpdaterManifestUrl = "https://raw.githubusercontent.com/yang-z-03/gated/refs/heads/master/.github/updater";
    public const string DisabledUpdaterMessage = "Updater service is disabled in this MSIX package. You should maintain the update via Microsoft Store";
#if DISABLE_UPDATER
    public static bool IsUpdaterDisabled { get; } = true;
#else
    public static bool IsUpdaterDisabled { get; } = false;
#endif

    private static readonly HttpClient http_client = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public async Task<UpdateInfo?> CheckForUpdatesAsync(CancellationToken cancellation_token = default)
    {
        var result = await GetUpdateStatusAsync(cancellation_token);
        return result.Update;
    }

    public async Task<UpdateCheckResult> GetUpdateStatusAsync(CancellationToken cancellation_token = default)
    {
        var versions = await fetch_versions_async(VersionsUrl, cancellation_token);
        var current = GetCurrentVersion();
        var os = GetCurrentSystemInfo();
        var latest_remote = versions.LastOrDefault();
        var latest_compatible = versions
            .Where(version => version.Archives.Count > 0)
            .Where(version => version.IsCompatibleWith(os))
            .LastOrDefault();
        var update = latest_compatible is not null && latest_compatible.Version > current
            ? new UpdateInfo(current, latest_compatible)
            : null;

        return new UpdateCheckResult(current, latest_remote, latest_compatible, os, update);
    }

    public async Task<string> DownloadChangelogAsync(UpdateVersion version, CancellationToken cancellation_token = default)
    {
        if (version.InfoHref is null)
            return "No changelog is available for this version.";

        await ensure_connection_async(version.InfoHref, cancellation_token);
        using var response = await http_client.GetAsync(version.InfoHref, cancellation_token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellation_token);
    }

    public async Task EnsureUpdaterCurrentAsync(
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellation_token = default)
    {
#if DISABLE_UPDATER
        await Task.CompletedTask;
        throw new NotSupportedException(DisabledUpdaterMessage);
#else
        string updater_path = get_updater_path();
        Directory.CreateDirectory(Path.GetDirectoryName(updater_path)!);
        progress?.Report(new UpdateProgress("Checking updater ...", "Fetching updater manifest.", null));
        var versions = await fetch_versions_async(UpdaterManifestUrl, cancellation_token);
        var system = GetCurrentSystemInfo();
        var latest = versions
            .Where(version => version.Archives.Count > 0)
            .Where(version => version.IsCompatibleWith(system))
            .OrderBy(version => version.Version)
            .LastOrDefault();

        if (latest is null)
            throw new InvalidDataException("Updater manifest does not contain a compatible updater.");

        var installed = get_installed_updater_version(updater_path);
        if (File.Exists(updater_path) && installed.CompareTo(latest.Version) >= 0)
        {
            progress?.Report(new UpdateProgress("Checking updater ...", $"Updater {installed} is current.", 1));
            return;
        }

        progress?.Report(new UpdateProgress("Updating updater ...", $"Installing updater {latest.Version}.", null));
        string staging_root = Path.Combine(Path.GetTempPath(), "gated-updater-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        string extract_root = Path.Combine(staging_root, "extract");
        Directory.CreateDirectory(staging_root);
        Directory.CreateDirectory(extract_root);

        try
        {
            for (int i = 0; i < latest.Archives.Count; i++)
            {
                var archive = latest.Archives[i];
                string path = Path.Combine(staging_root, $"updater-{i + 1}.zip");
                await download_archive(archive, path, i, latest.Archives.Count, progress, cancellation_token, "Downloading updater ...");
                string extract_path = safe_combine(extract_root, archive.ExtractPath);
                Directory.CreateDirectory(extract_path);
                ZipFile.ExtractToDirectory(path, extract_path, overwriteFiles: true);
            }

            copy_directory(extract_root, Path.GetDirectoryName(updater_path)!);
            if (!File.Exists(updater_path))
                throw new FileNotFoundException($"Updater package did not contain {PlatformSupport.UpdaterFileName}.", updater_path);
            ensure_executable_bit(updater_path);
        }
        finally
        {
            try
            {
                Directory.Delete(staging_root, recursive: true);
            }
            catch
            {
            }
        }
#endif
    }

    public void LaunchUpdater(UpdateInfo update)
    {
#if DISABLE_UPDATER
        throw new NotSupportedException(DisabledUpdaterMessage);
#else
        string app_path = get_application_path();
        string updater_path = get_updater_path();
        string? local_versions_path = get_local_versions_path();
        if (!File.Exists(updater_path))
            throw new FileNotFoundException("The updater executable was not found.", updater_path);
        ensure_executable_bit(updater_path);

        var arguments = new List<string>
        {
            "--app", quote(app_path),
            "--updater", quote(updater_path),
            "--runtime-root", quote(PlatformSupport.PersistenceDirectory),
            "--parent-pid", Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            "--current-version", update.Current.ToString(),
            "--target-version", update.Latest.Version.ToString(),
            "--versions-url", quote(VersionsUrl)
        };
        if (local_versions_path is not null)
            arguments.AddRange(["--local-versions", quote(local_versions_path)]);

        var start_info = new ProcessStartInfo
        {
            FileName = updater_path,
            UseShellExecute = true,
            Arguments = string.Join(" ", arguments)
        };
        Process.Start(start_info);
#endif
    }

    public void LaunchPythonBootstrapUpdater()
    {
#if DISABLE_UPDATER
        throw new NotSupportedException(DisabledUpdaterMessage);
#else
        string app_path = get_application_path();
        string updater_path = get_updater_path();
        string? local_versions_path = get_local_versions_path();
        if (!File.Exists(updater_path))
            throw new FileNotFoundException("The updater executable was not found.", updater_path);
        ensure_executable_bit(updater_path);

        var arguments = new List<string>
        {
            "--app", quote(app_path),
            "--updater", quote(updater_path),
            "--runtime-root", quote(PlatformSupport.PersistenceDirectory),
            "--parent-pid", Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            "--current-version", GetCurrentVersion().ToString(),
            "--versions-url", quote(VersionsUrl),
            "--requirements-only"
        };
        if (local_versions_path is not null)
            arguments.AddRange(["--local-versions", quote(local_versions_path)]);

        Process.Start(new ProcessStartInfo
        {
            FileName = updater_path,
            UseShellExecute = true,
            Arguments = string.Join(" ", arguments)
        });
#endif
    }

    public static AppVersion GetCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (AppVersion.TryParse(informational, out var parsed))
            return parsed;

        var version = assembly.GetName().Version;
        return version is null
            ? new AppVersion(0, 0, 0)
            : new AppVersion(version.Major, version.Minor, Math.Max(0, version.Build));
    }

    private static async Task download_archive(
        UpdateArchive archive,
        string target_path,
        int archive_index,
        int archive_count,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellation_token,
        string title)
    {
        progress?.Report(new UpdateProgress(title, $"Archive {archive_index + 1} of {archive_count}", 0));
        await ensure_connection_async(archive.Href, cancellation_token);
        using var response = await http_client.GetAsync(archive.Href, HttpCompletionOption.ResponseHeadersRead, cancellation_token);
        response.EnsureSuccessStatusCode();

        long? length = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellation_token);
        var buffer = new byte[1024 * 64];
        long total_read = 0;

        await using (var target = File.Create(target_path))
        {
            while (true)
            {
                int read = await source.ReadAsync(buffer, cancellation_token);
                if (read == 0)
                    break;

                await target.WriteAsync(buffer.AsMemory(0, read), cancellation_token);
                total_read += read;

                double? fraction = length > 0 ? (double)total_read / length.Value : null;
                progress?.Report(new UpdateProgress(
                    title,
                    $"{Path.GetFileName(archive.Href.LocalPath)} ({format_bytes(total_read)}{(length > 0 ? " / " + format_bytes(length.Value) : "")})",
                    fraction));
            }
        }

        using var zip = ZipFile.OpenRead(target_path);
        if (zip.Entries.Count == 0)
            throw new InvalidDataException("Downloaded update archive is empty.");
    }

    private static async Task<UpdateVersion[]> fetch_versions_async(string url, CancellationToken cancellation_token)
    {
        await ensure_connection_async(new Uri(url), cancellation_token);
        using var response = await http_client.GetAsync(url, cancellation_token);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellation_token);
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellation_token);
        return document.Root?.Elements("version")
            .Select(parse_version)
            .OrderBy(version => version.Version)
            .ToArray() ?? [];
    }

    private static UpdateVersion parse_version(XElement element)
    {
        int major = parse_required_int(element, "major");
        int minor = parse_required_int(element, "minor");
        int patch = parse_required_int(element, "patch");
        string? platform = (string?)element.Attribute("platform");
        string? arch = (string?)element.Attribute("arch");
        string? minimal = (string?)element.Attribute("minimal");
        Uri? info_href = element.Element("info")?.Attribute("href") is { } info_attribute
            ? new Uri(info_attribute.Value)
            : null;
        var archives = element.Elements("archive")
            .Select(archive => new UpdateArchive(
                new Uri((string?)archive.Attribute("href") ?? throw new FormatException("Archive href is missing.")),
                normalize_extract_path((string?)archive.Attribute("extract") ?? ".")))
            .ToArray();

        return new UpdateVersion(new AppVersion(major, minor, patch), platform, arch, minimal, info_href, archives);
    }

    public static SystemInfo GetCurrentSystemInfo()
    {
        return new SystemInfo(
            PlatformSupport.CurrentPlatform,
            PlatformSupport.CurrentArchitecture,
            Environment.OSVersion.Version,
            RuntimeInformation.OSDescription);
    }

    private static int parse_required_int(XElement element, string attribute)
    {
        string value = (string?)element.Attribute(attribute) ?? throw new FormatException($"Version {attribute} is missing.");
        return int.Parse(value, CultureInfo.InvariantCulture);
    }

    private static string normalize_extract_path(string path)
    {
        path = path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        return string.IsNullOrWhiteSpace(path) ? "." : path;
    }

    private static string get_application_path()
    {
        string? app_path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(app_path))
            throw new InvalidOperationException("Unable to locate the running application executable.");

        return app_path;
    }

    private static string get_updater_path() =>
        PlatformSupport.UpdaterPath;

    private static string? get_local_versions_path()
    {
        string? directory = Path.GetDirectoryName(get_application_path());
        while (!string.IsNullOrWhiteSpace(directory))
        {
            string candidate = Path.Combine(directory, ".github", "versions");
            if (File.Exists(candidate))
                return candidate;

            directory = Directory.GetParent(directory)?.FullName;
        }

        return null;
    }

    private static AppVersion get_installed_updater_version(string updater_path)
    {
        if (!File.Exists(updater_path))
            return new AppVersion(0, 0, 0);

        ensure_executable_bit(updater_path);
        if (try_get_updater_reported_version(updater_path, out var reported_version))
            return reported_version;

        var version_info = FileVersionInfo.GetVersionInfo(updater_path);
        if (AppVersion.TryParse(version_info.ProductVersion, out var product_version))
            return product_version;
        if (AppVersion.TryParse(version_info.FileVersion, out var file_version))
            return file_version;

        return new AppVersion(0, 0, 0);
    }

    private static bool try_get_updater_reported_version(string updater_path, out AppVersion version)
    {
        version = default;
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = updater_path,
                Arguments = "--version",
                WorkingDirectory = Path.GetDirectoryName(updater_path),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (process is null)
                return false;

            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return false;
            }

            return process.ExitCode == 0 && AppVersion.TryParse(output, out version);
        }
        catch
        {
            return false;
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

    private static string safe_combine(string root, string relative_path)
    {
        string combined = Path.GetFullPath(Path.Combine(root, relative_path));
        string full_root = Path.GetFullPath(root);
        if (!full_root.EndsWith(Path.DirectorySeparatorChar))
            full_root += Path.DirectorySeparatorChar;

        if (!combined.Equals(Path.GetFullPath(root), path_comparison()) &&
            !combined.StartsWith(full_root, path_comparison()))
            throw new InvalidDataException("Archive extract path is outside the target directory.");

        return combined;
    }

    private static void copy_directory(string source_root, string target_root)
    {
        foreach (string directory in Directory.EnumerateDirectories(source_root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source_root, directory);
            Directory.CreateDirectory(Path.Combine(target_root, relative));
        }

        foreach (string source in Directory.EnumerateFiles(source_root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source_root, source);
            string target = Path.Combine(target_root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
                File.SetAttributes(target, FileAttributes.Normal);
            File.Copy(source, target, overwrite: true);
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

    private static string quote(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static StringComparison path_comparison() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}

public readonly record struct AppVersion(int Major, int Minor, int Patch) : IComparable<AppVersion>
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

    public static bool operator >(AppVersion left, AppVersion right) => left.CompareTo(right) > 0;
    public static bool operator <(AppVersion left, AppVersion right) => left.CompareTo(right) < 0;

    public static bool TryParse(string? value, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var numeric = value.Split('+')[0].Split('-')[0];
        var parts = numeric.Split('.');
        if (parts.Length < 2)
            return false;

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minor))
            return false;

        int patch = 0;
        if (parts.Length > 2 && !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out patch))
            return false;

        version = new AppVersion(major, minor, patch);
        return true;
    }
}

public sealed record UpdateArchive(Uri Href, string ExtractPath);

public sealed record UpdateVersion(
    AppVersion Version,
    string? Platform,
    string? Arch,
    string? MinimalSystemVersion,
    Uri? InfoHref,
    IReadOnlyList<UpdateArchive> Archives)
{
    public bool IsCompatibleWith(SystemInfo system)
    {
        if (!string.IsNullOrWhiteSpace(Platform) &&
            !string.Equals(PlatformSupport.NormalizePlatform(Platform), system.Platform, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(Arch) &&
            !string.Equals(PlatformSupport.NormalizeArchitecture(Arch), system.Architecture, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(MinimalSystemVersion) &&
            System.Version.TryParse(MinimalSystemVersion, out var minimal) &&
            system.OsVersion.CompareTo(minimal) < 0)
            return false;

        return true;
    }
}

public sealed record UpdateInfo(AppVersion Current, UpdateVersion Latest);

public sealed record UpdateProgress(string Title, string Detail, double? Fraction);

public sealed record UpdateCheckResult(
    AppVersion Current,
    UpdateVersion? LatestRemote,
    UpdateVersion? LatestCompatible,
    SystemInfo System,
    UpdateInfo? Update);

public sealed record SystemInfo(string Platform, string Architecture, Version OsVersion, string Description)
{
    public string DisplayName => $"{Description} ({Architecture})";
}
