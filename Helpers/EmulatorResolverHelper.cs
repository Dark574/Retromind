using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Retromind.Helpers;

public static class EmulatorResolverHelper
{
    public static string? ResolveSystemWine()
    {
        var candidates = new[] { "wine", "wine64" };
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
        var fallbackDirs = new[] { "/usr/bin", "/usr/local/bin", 
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/bin") };
    
        var allDirs = pathDirs.Concat(fallbackDirs).Distinct().ToArray();

        foreach (var name in candidates)
        {
            foreach (var dir in allDirs)
            {
                var fullPath = Path.Combine(dir, name);
                if (File.Exists(fullPath))
                    return fullPath;
            }
        }
        return null;
    }
    
    public static ProcessStartInfo BuildWineInstallStartInfo(string winePath, string prefixPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = winePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            EnvironmentVariables =
            {
                // StringDictionary-konform & sicher
                ["WINEPREFIX"] = prefixPath,
                ["WINEDEBUG"] = "-all"
            }
        };

        return startInfo;
    }
}