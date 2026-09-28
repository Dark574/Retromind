using System;
using System.Diagnostics;
using System.IO;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services;

public enum WinePrefixRuntime
{
    Wine,
    Proton,
    Umu
}

/// <summary>
/// Resolves and scaffolds per-item Wine-compatible prefixes for launch.
/// </summary>
public sealed class WinePrefixService
{
    private readonly string _libraryRootPath;
    private readonly AppSettings _settings;

    public WinePrefixService(string libraryRootPath, AppSettings settings)
    {
        _libraryRootPath = libraryRootPath ?? throw new ArgumentNullException(nameof(libraryRootPath));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public void Prepare(
        MediaItem item,
        ProcessStartInfo startInfo,
        WinePrefixRuntime runtime)
    {
        var prefixPath = ResolvePrefixPath(item, out var relativePrefixPathToSave);
        var prefixRoot = prefixPath;
        var winePrefixPath = prefixPath;

        switch (runtime)
        {
            case WinePrefixRuntime.Umu:
                (prefixRoot, winePrefixPath) = ResolveUmuPaths(prefixPath);
                break;

            case WinePrefixRuntime.Proton:
                (prefixRoot, winePrefixPath) = ResolveProtonPaths(prefixPath);
                break;

            case WinePrefixRuntime.Wine:
                (prefixRoot, winePrefixPath) = ResolveWinePaths(prefixPath);
                break;
        }

        Directory.CreateDirectory(prefixRoot);

        // UMU owns <compat-root>/pfx. Only scaffold its compat root until umu-run creates that link/folder.
        var scaffoldPrefixPath = runtime == WinePrefixRuntime.Umu &&
                                 !string.Equals(winePrefixPath, prefixRoot, StringComparison.OrdinalIgnoreCase)
            ? prefixRoot
            : winePrefixPath;
        Directory.CreateDirectory(scaffoldPrefixPath);

        var dosDevicesDirectory = Path.Combine(scaffoldPrefixPath, "dosdevices");
        Directory.CreateDirectory(dosDevicesDirectory);

        var isUmuCompatRootScaffold =
            runtime == WinePrefixRuntime.Umu &&
            string.Equals(scaffoldPrefixPath, prefixRoot, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(winePrefixPath, prefixRoot, StringComparison.OrdinalIgnoreCase);

        if (!isUmuCompatRootScaffold)
        {
            Directory.CreateDirectory(Path.Combine(scaffoldPrefixPath, "drive_c"));
            PrefixPathHelper.EnsureDosDeviceMapping(dosDevicesDirectory, "c:", "../drive_c");
        }

        EnsurePortableGamesMapping(prefixRoot, dosDevicesDirectory);

        if (runtime is WinePrefixRuntime.Proton or WinePrefixRuntime.Umu)
            startInfo.EnvironmentVariables["STEAM_COMPAT_DATA_PATH"] = prefixRoot;

        startInfo.EnvironmentVariables["WINEPREFIX"] = runtime == WinePrefixRuntime.Umu
            ? prefixRoot
            : winePrefixPath;

        if (relativePrefixPathToSave != null)
            item.PrefixPath = relativePrefixPathToSave;
    }

    private string ResolvePrefixPath(MediaItem item, out string? relativePrefixPathToSave)
    {
        relativePrefixPathToSave = null;
        if (!string.IsNullOrWhiteSpace(item.PrefixPath))
        {
            var storedPath = item.PrefixPath.Trim();
            if (_settings.PreferPortableLaunchPaths)
            {
                var portableStoredPath =
                    PrefixPathHelper.ConvertPathToLibraryRelativeIfInsideLibraryRoot(storedPath, _libraryRootPath);
                if (!string.Equals(portableStoredPath, storedPath, StringComparison.Ordinal))
                {
                    storedPath = portableStoredPath ?? storedPath;
                    relativePrefixPathToSave = storedPath;
                }
            }

            return PrefixPathHelper.ResolveAbsolutePrefixPath(storedPath, _libraryRootPath);
        }

        var safeTitle = PrefixPathHelper.SanitizePrefixFolderName(item.Title);
        relativePrefixPathToSave = Path.Combine("Prefixes", $"{item.Id}_{safeTitle}");
        return Path.Combine(_libraryRootPath, relativePrefixPathToSave);
    }

    private static (string PrefixRoot, string WinePrefixPath) ResolveUmuPaths(string prefixPath)
    {
        var prefixRoot = prefixPath;
        string pfxPath;
        if (PrefixPathHelper.IsPfxPath(prefixPath))
        {
            pfxPath = prefixPath;
            prefixRoot = Directory.GetParent(prefixPath)?.FullName ?? prefixPath;
        }
        else
        {
            pfxPath = Path.Combine(prefixPath, "pfx");
        }

        var rootInitialized = PrefixPathHelper.IsWinePrefixInitialized(prefixRoot);
        var pfxInitialized = PrefixPathHelper.IsWinePrefixInitialized(pfxPath);
        var winePrefixPath = rootInitialized && !pfxInitialized ? prefixRoot : pfxPath;
        return (prefixRoot, winePrefixPath);
    }

    private static (string PrefixRoot, string WinePrefixPath) ResolveProtonPaths(string prefixPath)
    {
        if (PrefixPathHelper.IsPfxPath(prefixPath))
            return (Directory.GetParent(prefixPath)?.FullName ?? prefixPath, prefixPath);

        var pfxPath = Path.Combine(prefixPath, "pfx");
        var rootInitialized = PrefixPathHelper.IsWinePrefixInitialized(prefixPath);
        var pfxInitialized = PrefixPathHelper.IsWinePrefixInitialized(pfxPath);
        return rootInitialized && !pfxInitialized
            ? (prefixPath, prefixPath)
            : (prefixPath, pfxPath);
    }

    private static (string PrefixRoot, string WinePrefixPath) ResolveWinePaths(string prefixPath)
    {
        if (PrefixPathHelper.IsPfxPath(prefixPath))
            return (Directory.GetParent(prefixPath)?.FullName ?? prefixPath, prefixPath);

        var pfxPath = Path.Combine(prefixPath, "pfx");
        var usesNestedPfx = !Directory.Exists(Path.Combine(prefixPath, "drive_c")) &&
                            (Directory.Exists(Path.Combine(pfxPath, "drive_c")) || Directory.Exists(pfxPath));
        return (prefixPath, usesNestedPfx ? pfxPath : prefixPath);
    }

    private void EnsurePortableGamesMapping(string prefixRoot, string dosDevicesDirectory)
    {
        var libraryRoot = Path.GetFullPath(_libraryRootPath);
        var prefixFullPath = Path.GetFullPath(prefixRoot);
        var libraryRootWithSeparator = libraryRoot.EndsWith(Path.DirectorySeparatorChar)
            ? libraryRoot
            : libraryRoot + Path.DirectorySeparatorChar;

        var isPrefixInsideLibrary =
            prefixFullPath.StartsWith(libraryRootWithSeparator, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(prefixFullPath, libraryRoot, StringComparison.OrdinalIgnoreCase);
        if (!isPrefixInsideLibrary)
            return;

        var gamesRoot = Path.Combine(libraryRoot, "Games");
        Directory.CreateDirectory(gamesRoot);

        var relativeTarget = Path.GetRelativePath(dosDevicesDirectory, gamesRoot);
        PrefixPathHelper.EnsureDosDeviceMapping(dosDevicesDirectory, "d:", relativeTarget);
    }
}
