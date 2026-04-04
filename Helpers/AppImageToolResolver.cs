using System;
using System.IO;

namespace Retromind.Helpers;

/// <summary>
/// Resolves executables bundled inside an AppImage runtime (APPDIR).
/// </summary>
public static class AppImageToolResolver
{
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
}
