using System;
using System.Collections.Generic;
using System.IO;

namespace Retromind.Helpers;

public static class EnvironmentPathHelper
{
    private const string PasswdPath = "/etc/passwd";

    private static readonly HashSet<string> DataRootPathKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "PROTONPATH",
        "STEAM_COMPAT_DATA_PATH",
        "HOME",
        "DOTNET_CLI_HOME",
        "XDG_CONFIG_HOME",
        "XDG_DATA_HOME",
        "XDG_CACHE_HOME",
        "XDG_STATE_HOME"
    };

    public static bool IsDataRootPathKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        return DataRootPathKeys.Contains(key);
    }

    public static string NormalizeDataRootPathIfNeeded(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key) || !IsDataRootPathKey(key))
            return value ?? string.Empty;

        var raw = value ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw) || Path.IsPathRooted(raw))
            return raw;

        return AppPaths.ResolveDataPath(raw);
    }

    internal static string? TryGetRealUserHomePath()
        => TryGetUserHomePathFromPasswd(Environment.UserName, PasswdPath);

    internal static string? TryGetUserHomePathFromPasswd(string? userName, string passwdPath)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(passwdPath))
            return null;

        try
        {
            if (!File.Exists(passwdPath))
                return null;

            foreach (var line in File.ReadLines(passwdPath))
            {
                if (!line.StartsWith(userName + ":", StringComparison.Ordinal))
                    continue;

                var parts = line.Split(':');
                return parts.Length > 5 && !string.IsNullOrWhiteSpace(parts[5])
                    ? parts[5]
                    : null;
            }
        }
        catch
        {
            // Best-effort: callers can continue without the host home fallback.
        }

        return null;
    }

    internal static string? TryFindExecutableInCurrentPath(string executableName)
        => TryFindExecutableInPath(executableName, Environment.GetEnvironmentVariable("PATH"));

    internal static string? TryFindExecutableInPath(string executableName, string? pathValue)
    {
        if (string.IsNullOrWhiteSpace(executableName) || string.IsNullOrWhiteSpace(pathValue))
            return null;

        foreach (var segment in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = segment.Trim();
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            string candidatePath;
            try
            {
                candidatePath = Path.Combine(directory, executableName);
            }
            catch
            {
                continue;
            }

            if (File.Exists(candidatePath))
                return candidatePath;
        }

        return null;
    }

    internal static string? ResolveExecutablePathForExistenceCheck(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (Path.IsPathRooted(path))
            return Path.GetFullPath(path);

        if (!path.Contains(Path.DirectorySeparatorChar) &&
            !path.Contains(Path.AltDirectorySeparatorChar))
        {
            return null;
        }

        return AppPaths.ResolveDataPath(path);
    }
}
