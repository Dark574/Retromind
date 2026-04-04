using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Retromind.Helpers;

/// <summary>
/// Sanitizes ProcessStartInfo for host desktop helper processes (e.g. xdg-open).
/// Prevents inheriting AppImage and portable HOME/XDG environment into host tools.
/// </summary>
public static class HostProcessEnvironmentSanitizer
{
    private const string PasswdPath = "/etc/passwd";

    public static void Sanitize(ProcessStartInfo psi)
    {
        if (psi == null)
            return;

        // Avoid AppImage-shipped libs shadowing host libs for desktop helpers.
        psi.Environment.Remove("LD_LIBRARY_PATH");
        psi.Environment.Remove("VLC_PLUGIN_PATH");

        // Remove AppImage-injected PATH segments (APPDIR/usr/bin) so host helpers resolve from system PATH.
        SanitizePath(psi);

        // If Retromind runs with portable HOME/XDG, do not leak those into host desktop apps.
        var portableHomeRoot = NormalizePath(Path.Combine(AppPaths.DataRoot, "Home"));
        if (string.IsNullOrWhiteSpace(portableHomeRoot))
            return;

        RemoveIfUnderPortableHome(psi, "XDG_CONFIG_HOME", portableHomeRoot);
        RemoveIfUnderPortableHome(psi, "XDG_DATA_HOME", portableHomeRoot);
        RemoveIfUnderPortableHome(psi, "XDG_CACHE_HOME", portableHomeRoot);
        RemoveIfUnderPortableHome(psi, "XDG_STATE_HOME", portableHomeRoot);
        RemoveIfUnderPortableHome(psi, "DOTNET_CLI_HOME", portableHomeRoot);

        if (!psi.Environment.TryGetValue("HOME", out var home) || string.IsNullOrWhiteSpace(home))
            return;

        if (!IsUnderPortableHome(home, portableHomeRoot))
            return;

        var realHome = TryGetRealUserHomePath();
        if (!string.IsNullOrWhiteSpace(realHome))
            psi.Environment["HOME"] = realHome;
        else
            psi.Environment.Remove("HOME");
    }

    private static void SanitizePath(ProcessStartInfo psi)
    {
        if (!psi.Environment.TryGetValue("PATH", out var pathValue) || string.IsNullOrWhiteSpace(pathValue))
            return;

        var appDir = Environment.GetEnvironmentVariable("APPDIR");
        if (string.IsNullOrWhiteSpace(appDir))
            return;

        var appDirNorm = NormalizePath(appDir);
        if (string.IsNullOrWhiteSpace(appDirNorm))
            return;

        var separator = Path.PathSeparator;
        var filteredSegments = pathValue
            .Split(separator, StringSplitOptions.RemoveEmptyEntries)
            .Where(segment =>
            {
                var segmentNorm = NormalizePath(segment);
                if (string.IsNullOrWhiteSpace(segmentNorm))
                    return false;

                if (segmentNorm.Equals(appDirNorm, StringComparison.Ordinal))
                    return false;

                return !segmentNorm.StartsWith(appDirNorm + "/", StringComparison.Ordinal);
            })
            .ToArray();

        if (filteredSegments.Length == 0)
            return;

        psi.Environment["PATH"] = string.Join(separator, filteredSegments);
    }

    private static void RemoveIfUnderPortableHome(ProcessStartInfo psi, string key, string portableHomeRoot)
    {
        if (!psi.Environment.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            return;

        if (IsUnderPortableHome(value, portableHomeRoot))
            psi.Environment.Remove(key);
    }

    private static bool IsUnderPortableHome(string value, string portableHomeRoot)
    {
        var normalized = NormalizePath(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return normalized.Equals(portableHomeRoot, StringComparison.Ordinal) ||
               normalized.StartsWith(portableHomeRoot + "/", StringComparison.Ordinal);
    }

    private static string NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Replace('\\', '/').Trim();
        while (normalized.EndsWith("/", StringComparison.Ordinal))
            normalized = normalized[..^1];

        return normalized;
    }

    private static string? TryGetRealUserHomePath()
    {
        try
        {
            var userName = Environment.UserName;
            if (string.IsNullOrWhiteSpace(userName))
                return null;

            if (!File.Exists(PasswdPath))
                return null;

            foreach (var line in File.ReadLines(PasswdPath))
            {
                if (!line.StartsWith(userName + ":", StringComparison.Ordinal))
                    continue;

                var parts = line.Split(':');
                if (parts.Length <= 5 || string.IsNullOrWhiteSpace(parts[5]))
                    return null;

                return parts[5];
            }
        }
        catch
        {
            // Best-effort.
        }

        return null;
    }
}
