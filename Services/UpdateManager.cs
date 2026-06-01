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
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace gated.Services;

public sealed class UpdateManager
{
    public const string VersionsUrl = "https://raw.githubusercontent.com/yang-z-03/gated/refs/heads/master/.github/versions";

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
        using var response = await http_client.GetAsync(VersionsUrl, cancellation_token);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellation_token);
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellation_token);
        var versions = document.Root?.Elements("version")
            .Select(parse_version)
            .OrderBy(version => version.Version)
            .ToArray() ?? [];

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

        using var response = await http_client.GetAsync(version.InfoHref, cancellation_token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellation_token);
    }

    public async Task<string> DownloadUpdateAsync(
        UpdateInfo update,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellation_token = default)
    {
        string staging_root = Path.Combine(Path.GetTempPath(), "gated-update-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(staging_root);

        var archives = new List<StagedArchive>();
        for (int i = 0; i < update.Latest.Archives.Count; i++)
        {
            var archive = update.Latest.Archives[i];
            string path = Path.Combine(staging_root, $"archive-{i + 1}.zip");
            await download_archive(archive, path, i, update.Latest.Archives.Count, progress, cancellation_token);
            archives.Add(new StagedArchive(path, archive.ExtractPath));
        }

        string manifest_path = Path.Combine(staging_root, "update.xml");
        var manifest = new XDocument(
            new XElement("update",
                new XAttribute("version", update.Latest.Version.ToString()),
                archives.Select(archive =>
                    new XElement("archive",
                        new XAttribute("path", archive.Path),
                        new XAttribute("extract", archive.ExtractPath)))));
        manifest.Save(manifest_path);
        return manifest_path;
    }

    public void LaunchUpdater(string manifest_path)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Automatic extraction is currently supported on Windows.");

        string? app_path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(app_path))
            throw new InvalidOperationException("Unable to locate the running application executable.");

        string script_path = Path.Combine(Path.GetDirectoryName(manifest_path)!, "apply-update.ps1");
        File.WriteAllText(script_path, create_windows_updater_script(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var start_info = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = true,
            Arguments = string.Join(" ", new[]
            {
                "-NoProfile",
                "-ExecutionPolicy", "Bypass",
                "-STA",
                "-File", quote(script_path),
                "-ManifestPath", quote(manifest_path),
                "-AppPath", quote(app_path),
                "-ParentPid", Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
            })
        };
        Process.Start(start_info);
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
        CancellationToken cancellation_token)
    {
        progress?.Report(new UpdateProgress("Downloading update ...", $"Archive {archive_index + 1} of {archive_count}", 0));
        using var response = await http_client.GetAsync(archive.Href, HttpCompletionOption.ResponseHeadersRead, cancellation_token);
        response.EnsureSuccessStatusCode();

        long? length = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellation_token);
        await using var target = File.Create(target_path);
        var buffer = new byte[1024 * 64];
        long total_read = 0;

        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellation_token);
            if (read == 0)
                break;

            await target.WriteAsync(buffer.AsMemory(0, read), cancellation_token);
            total_read += read;

            double? fraction = length > 0 ? (double)total_read / length.Value : null;
            progress?.Report(new UpdateProgress(
                "Downloading update ...",
                $"{Path.GetFileName(archive.Href.LocalPath)} ({format_bytes(total_read)}{(length > 0 ? " / " + format_bytes(length.Value) : "")})",
                fraction));
        }

        using var zip = ZipFile.OpenRead(target_path);
        if (zip.Entries.Count == 0)
            throw new InvalidDataException("Downloaded update archive is empty.");
    }

    private static UpdateVersion parse_version(XElement element)
    {
        int major = parse_required_int(element, "major");
        int minor = parse_required_int(element, "minor");
        int patch = parse_required_int(element, "patch");
        string? platform = (string?)element.Attribute("platform");
        string? minimal = (string?)element.Attribute("minimal");
        Uri? info_href = element.Element("info")?.Attribute("href") is { } info_attribute
            ? new Uri(info_attribute.Value)
            : null;
        var archives = element.Elements("archive")
            .Select(archive => new UpdateArchive(
                new Uri((string?)archive.Attribute("href") ?? throw new FormatException("Archive href is missing.")),
                normalize_extract_path((string?)archive.Attribute("extract") ?? ".")))
            .ToArray();

        return new UpdateVersion(new AppVersion(major, minor, patch), platform, minimal, info_href, archives);
    }

    public static SystemInfo GetCurrentSystemInfo()
    {
        string platform =
            OperatingSystem.IsWindows() ? "windows" :
            OperatingSystem.IsMacOS() ? "macos" :
            OperatingSystem.IsLinux() ? "linux" :
            "unknown";

        return new SystemInfo(platform, Environment.OSVersion.Version, RuntimeInformation.OSDescription);
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

    private static string quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string create_windows_updater_script() =>
        """
        param(
            [Parameter(Mandatory=$true)][string]$ManifestPath,
            [Parameter(Mandatory=$true)][string]$AppPath,
            [Parameter(Mandatory=$true)][int]$ParentPid
        )

        Add-Type -AssemblyName PresentationFramework
        Add-Type -AssemblyName System.IO.Compression.FileSystem

        $ErrorActionPreference = "Stop"
        $installRoot = [System.IO.Path]::GetDirectoryName($AppPath)
        $document = [xml](Get-Content -LiteralPath $ManifestPath)
        $archives = @($document.update.archive)

        function Test-InsidePath([string]$Path, [string]$Root) {
            $fullPath = [System.IO.Path]::GetFullPath($Path)
            $fullRoot = [System.IO.Path]::GetFullPath($Root)
            if ($fullPath.Equals($fullRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
            if (!$fullRoot.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
                $fullRoot += [System.IO.Path]::DirectorySeparatorChar
            }
            return $fullPath.StartsWith($fullRoot, [System.StringComparison]::OrdinalIgnoreCase)
        }

        $window = New-Object Windows.Window
        $window.Title = "Updating Gated"
        $window.Width = 460
        $window.Height = 170
        $window.WindowStartupLocation = "CenterScreen"
        $window.ResizeMode = "NoResize"
        $window.Background = "#303030"

        $panel = New-Object Windows.Controls.StackPanel
        $panel.Margin = "22"
        $panel.VerticalAlignment = "Center"
        $panel.Orientation = "Vertical"

        $title = New-Object Windows.Controls.TextBlock
        $title.Text = "Installing update ..."
        $title.Foreground = "White"
        $title.FontWeight = "SemiBold"
        $title.Margin = "0,0,0,10"

        $subtitle = New-Object Windows.Controls.TextBlock
        $subtitle.Text = "Waiting for Gated to close."
        $subtitle.Foreground = "#DADDE4"
        $subtitle.TextWrapping = "Wrap"
        $subtitle.Margin = "0,0,0,12"

        $progress = New-Object Windows.Controls.ProgressBar
        $progress.Minimum = 0
        $progress.Maximum = 100
        $progress.Height = 18
        $progress.Value = 0

        $panel.Children.Add($title) | Out-Null
        $panel.Children.Add($subtitle) | Out-Null
        $panel.Children.Add($progress) | Out-Null
        $window.Content = $panel

        $window.Add_ContentRendered({
            try {
                try {
                    $parent = Get-Process -Id $ParentPid -ErrorAction Stop
                    $parent.WaitForExit()
                } catch { }

                $totalEntries = 0
                foreach ($archive in $archives) {
                    $zip = [System.IO.Compression.ZipFile]::OpenRead($archive.path)
                    $totalEntries += $zip.Entries.Count
                    $zip.Dispose()
                }
                if ($totalEntries -eq 0) { throw "Update archive did not contain files." }

                $completed = 0
                foreach ($archive in $archives) {
                    $extractRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($installRoot, [string]$archive.extract))
                    if (!(Test-InsidePath $extractRoot $installRoot)) {
                        throw "Archive extract path is outside the installation directory."
                    }

                    $zip = [System.IO.Compression.ZipFile]::OpenRead($archive.path)
                    try {
                        foreach ($entry in $zip.Entries) {
                            $target = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($extractRoot, $entry.FullName))
                            if (!(Test-InsidePath $target $extractRoot)) {
                                throw "Archive entry is outside the target directory."
                            }

                            if ([string]::IsNullOrEmpty($entry.Name)) {
                                [System.IO.Directory]::CreateDirectory($target) | Out-Null
                            } else {
                                [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($target)) | Out-Null
                                $subtitle.Text = "Extracting " + $entry.FullName
                                [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
                            }

                            $completed += 1
                            $progress.Value = [Math]::Min(100, [Math]::Round(($completed / $totalEntries) * 100))
                            [Windows.Threading.Dispatcher]::CurrentDispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::Background)
                        }
                    } finally {
                        $zip.Dispose()
                    }
                }

                $subtitle.Text = "Restarting Gated ..."
                Start-Process -FilePath $AppPath -WorkingDirectory $installRoot
                $window.Close()
            } catch {
                $title.Text = "Update failed"
                $subtitle.Text = $_.Exception.Message
                $progress.Visibility = "Collapsed"
            }
        })

        $window.ShowDialog() | Out-Null
        """;
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
    string? MinimalSystemVersion,
    Uri? InfoHref,
    IReadOnlyList<UpdateArchive> Archives)
{
    public bool IsCompatibleWith(SystemInfo system)
    {
        if (!string.IsNullOrWhiteSpace(Platform) &&
            !string.Equals(normalize_platform(Platform), system.Platform, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(MinimalSystemVersion) &&
            System.Version.TryParse(MinimalSystemVersion, out var minimal) &&
            system.OsVersion.CompareTo(minimal) < 0)
            return false;

        return true;
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

public sealed record UpdateInfo(AppVersion Current, UpdateVersion Latest);

public sealed record StagedArchive(string Path, string ExtractPath);

public sealed record UpdateProgress(string Title, string Detail, double? Fraction);

public sealed record UpdateCheckResult(
    AppVersion Current,
    UpdateVersion? LatestRemote,
    UpdateVersion? LatestCompatible,
    SystemInfo System,
    UpdateInfo? Update);

public sealed record SystemInfo(string Platform, Version OsVersion, string Description)
{
    public string DisplayName => $"{Platform} {OsVersion} ({Description})";
}
