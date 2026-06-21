using System;
using System.IO;
using System.Runtime.InteropServices;

namespace gated.Shared;

public static class PlatformSupport
{
    public static string CurrentPlatform =>
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsMacOS() ? "macos" :
        OperatingSystem.IsLinux() ? "linux" :
        "unknown";

    public static string CurrentArchitecture => NormalizeArchitecture(RuntimeInformation.ProcessArchitecture.ToString());

    public static string UpdaterFileName => OperatingSystem.IsWindows() ? "update.exe" : "update";

    public static string PersistenceDirectory
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gated");

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (OperatingSystem.IsMacOS())
                return Path.Combine(home, "Library", "Application Support", "Gated");

            return Path.Combine(home, ".local", "Gated");
        }
    }

    public static string EmbeddedPythonLibraryPath(string application_root) =>
        Path.Combine(application_root, EmbeddedPythonLibraryRelativePath);

    public static string EmbeddedPythonHome(string application_root) =>
        Path.Combine(application_root, "python");

    public static string EmbeddedPythonExecutablePath(string application_root) =>
        Path.Combine(application_root, EmbeddedPythonExecutableRelativePath);

    public static string NormalizePlatform(string? platform) =>
        (platform ?? "").Trim().ToLowerInvariant() switch
        {
            "win" or "windows" => "windows",
            "mac" or "macos" or "osx" or "darwin" => "macos",
            "linux" or "posix" => "linux",
            var value => value
        };

    public static string NormalizeArchitecture(string? architecture) =>
        (architecture ?? "").Trim().ToLowerInvariant() switch
        {
            "amd64" or "x86_64" => "x64",
            "aarch64" => "arm64",
            var value => value
        };

    private static string EmbeddedPythonLibraryRelativePath =>
        OperatingSystem.IsWindows()
            ? Path.Combine("python", "python313.dll")
            : OperatingSystem.IsMacOS()
                ? Path.Combine("python", "lib", "libpython3.13.dylib")
                : Path.Combine("python", "lib", "libpython3.13.so");

    private static string EmbeddedPythonExecutableRelativePath =>
        OperatingSystem.IsWindows()
            ? Path.Combine("python", "python.exe")
            : Path.Combine("python", "bin", "python3");
}
