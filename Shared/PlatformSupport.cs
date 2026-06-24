using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace gated.Shared;

public sealed record RequiredPythonPackage(string Name, string VersionCondition = "");

public static class PlatformSupport
{
    public static IReadOnlyList<RequiredPythonPackage> RequiredPythonPackages { get; } =
    [
        new("jedi", "==0.20.0"),
        new("parso", "==0.8.7"),
        new("docstring_parser", "==0.18.0"),
        new("igraph", ">=1,<2"),
        new("scipy", ">=1,<2"),
        new("numpy", ">=2,<3"),
        new("pandas", ">=3,<4"),
        new("scikit-learn", ">=1.9,<1.10"),
        new("leidenalg", ">=0.12,<0.13"),
        new("umap-learn", ">=0.5,<0.6"),

        // CUDA 13 and GPU acceleration
        // new("cudf-cu13", "==25.10.*"),
        // new("cuml-cu13", "==25.10.*"),
        // new("cugraph-cu13", "==25.10.*"),
        // new("cuvs-cu13", "==25.10.*"),
        // new("cupy-cuda13x", "==25.10.*"),

        new("rapids-singlecell"),
        new("torch", ">=2"),
        new("anndata")
    ];

    public static string CurrentPlatform =>
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsMacOS() ? "macos" :
        OperatingSystem.IsLinux() ? "linux" :
        "unknown";

    public static string EnvironmentPathSeparator =>
        CurrentPlatform == "windows" ? ";" : ":";

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
