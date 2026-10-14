using System;
using System.Collections.Generic;
using System.IO;

namespace Retromind.Services.Stores.Epic.Legendary;

internal sealed class LegendaryExecutableResolver
{
    internal const string ExecutableName = "legendary";

    private readonly string _dataRoot;
    private readonly string _appBaseDirectory;
    private readonly string? _appDir;
    private readonly string? _pathEnvironment;

    internal LegendaryExecutableResolver(string dataRoot)
        : this(
            dataRoot,
            AppContext.BaseDirectory,
            Environment.GetEnvironmentVariable("APPDIR"),
            Environment.GetEnvironmentVariable("PATH"))
    {
    }

    internal LegendaryExecutableResolver(
        string dataRoot,
        string appBaseDirectory,
        string? appDir,
        string? pathEnvironment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(appBaseDirectory);

        _dataRoot = Path.GetFullPath(dataRoot);
        _appBaseDirectory = Path.GetFullPath(appBaseDirectory);
        _appDir = string.IsNullOrWhiteSpace(appDir) ? null : Path.GetFullPath(appDir);
        _pathEnvironment = pathEnvironment;
    }

    internal string? Resolve()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in GetCandidates())
        {
            var fullPath = NormalizeCandidate(candidate);
            if (fullPath == null || !seen.Add(fullPath) || !File.Exists(fullPath))
                continue;

            return fullPath;
        }

        return null;
    }

    internal string GetRequiredExecutable()
    {
        return Resolve() ?? throw new LegendaryCliException(
            "Legendary is not available. The managed Legendary executable has not been installed.");
    }

    internal static string GetManagedExecutablePath(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        return Path.Combine(Path.GetFullPath(dataRoot), "Tools", "Legendary", ExecutableName);
    }

    private IEnumerable<string> GetCandidates()
    {
        // A managed copy takes precedence so Retromind controls the Legendary version it uses.
        yield return GetManagedExecutablePath(_dataRoot);

        if (_appDir != null)
        {
            yield return Path.Combine(_appDir, "usr", "bin", ExecutableName);
            yield return Path.Combine(_appDir, "bin", ExecutableName);
            yield return Path.Combine(_appDir, ExecutableName);
        }

        yield return Path.Combine(_appBaseDirectory, "Tools", "Legendary", ExecutableName);

        if (string.IsNullOrWhiteSpace(_pathEnvironment))
            yield break;

        foreach (var directory in _pathEnvironment.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Path.IsPathRooted(directory))
                yield return Path.Combine(directory, ExecutableName);
        }
    }

    private static string? NormalizeCandidate(string candidate)
    {
        try
        {
            return Path.GetFullPath(candidate);
        }
        catch
        {
            return null;
        }
    }
}
