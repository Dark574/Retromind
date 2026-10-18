using System;
using System.Collections.Generic;
using System.IO;

namespace Retromind.Services.Stores.Gog.Comet;

internal sealed class CometExecutableResolver
{
    internal const string ExecutableName = "comet";

    private readonly string _appBaseDirectory;
    private readonly string? _appDir;

    internal CometExecutableResolver()
        : this(
            AppContext.BaseDirectory,
            Environment.GetEnvironmentVariable("APPDIR"))
    {
    }

    internal CometExecutableResolver(string appBaseDirectory, string? appDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appBaseDirectory);

        _appBaseDirectory = Path.GetFullPath(appBaseDirectory);
        _appDir = string.IsNullOrWhiteSpace(appDir) ? null : Path.GetFullPath(appDir);
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
        return Resolve() ?? throw new CometProcessException(
            "Comet is not available in this Retromind installation.");
    }

    private IEnumerable<string> GetCandidates()
    {
        if (_appDir != null)
        {
            yield return Path.Combine(_appDir, "usr", "bin", ExecutableName);
            yield return Path.Combine(_appDir, "bin", ExecutableName);
            yield return Path.Combine(_appDir, ExecutableName);
        }

        // Published non-AppImage layouts keep bundled helpers beside Retromind.
        yield return Path.Combine(_appBaseDirectory, ExecutableName);
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
