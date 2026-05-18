using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Retromind.Helpers;

/// <summary>
/// Resolves executables bundled inside an AppImage runtime (APPDIR).
/// </summary>
public static class AppImageToolResolver
{
    private const string LdLibraryPathKey = "LD_LIBRARY_PATH";

    public static bool IsAppImageRuntime()
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APPIMAGE")) ||
           !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APPDIR"));

    public static string? ResolveBundledExecutable(string executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName))
            return null;

        var appDir = Environment.GetEnvironmentVariable("APPDIR");
        if (string.IsNullOrWhiteSpace(appDir))
            return null;

        var toolName = Path.GetFileName(executableName.Trim());
        if (string.IsNullOrWhiteSpace(toolName))
            return null;

        var candidates = new[]
        {
            Path.Combine(appDir, "usr", "bin", toolName),
            Path.Combine(appDir, "bin", toolName),
            Path.Combine(appDir, toolName)
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Ensures a bundled AppImage helper process can resolve its private runtime libraries.
    /// </summary>
    public static void ConfigureBundledToolEnvironment(ProcessStartInfo startInfo, string? executablePath)
    {
        if (startInfo == null || string.IsNullOrWhiteSpace(executablePath))
            return;

        var appDir = Environment.GetEnvironmentVariable("APPDIR");
        if (string.IsNullOrWhiteSpace(appDir))
            return;

        var appDirNormalized = NormalizePath(appDir);
        var executableNormalized = NormalizePath(executablePath);
        if (string.IsNullOrWhiteSpace(appDirNormalized) ||
            string.IsNullOrWhiteSpace(executableNormalized) ||
            !executableNormalized.StartsWith(appDirNormalized + "/", StringComparison.Ordinal))
        {
            return;
        }

        var bundledLibDir = Path.Combine(appDir, "usr", "lib");
        if (!Directory.Exists(bundledLibDir))
            return;

        var combined = new List<string> { bundledLibDir };

        if (startInfo.Environment.TryGetValue(LdLibraryPathKey, out var existingFromStartInfo) &&
            !string.IsNullOrWhiteSpace(existingFromStartInfo))
        {
            combined.Add(existingFromStartInfo);
        }
        else
        {
            var inherited = Environment.GetEnvironmentVariable(LdLibraryPathKey);
            if (!string.IsNullOrWhiteSpace(inherited))
                combined.Add(inherited);
        }

        startInfo.Environment[LdLibraryPathKey] = string.Join(":", combined);
    }

    private static string NormalizePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Replace('\\', '/').Trim();
        while (normalized.EndsWith("/", StringComparison.Ordinal))
            normalized = normalized[..^1];
        return normalized;
    }
}
