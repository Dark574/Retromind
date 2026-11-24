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
public sealed partial class WinePrefixService
{
    private readonly string _libraryRootPath;
    private readonly AppSettings _settings;

    public WinePrefixService(string libraryRootPath, AppSettings settings)
    {
        _libraryRootPath = Path.GetFullPath(libraryRootPath ?? throw new ArgumentNullException(nameof(libraryRootPath)));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public void Prepare(
        MediaItem item,
        ProcessStartInfo startInfo,
        WinePrefixRuntime runtime) => Prepare(item, startInfo, runtime, scaffold: true);

    internal void Prepare(MediaItem item, ProcessStartInfo startInfo, WinePrefixRuntime runtime, bool scaffold) =>
        PrepareLaunch(item, startInfo, runtime, applyManagedPrefix: true, scaffold: scaffold);

    internal PrefixResolution? PrepareLaunch(MediaItem item, ProcessStartInfo startInfo, WinePrefixRuntime runtime,
        bool applyManagedPrefix, bool scaffold, bool wineOverride = false, bool compatOverride = false,
        bool runtimeUncertain = false)
    {
        string? pathToSave = null;
        PrefixResolution? configured = null;
        if (applyManagedPrefix)
        {
            configured = ResolveConfigured(ResolvePrefixPath(item, out pathToSave), runtime);

            // Explicit launch overrides have already been applied. Managed defaults
            // replace inherited host values only, leaving explicit values intact.
            if (runtime is WinePrefixRuntime.Umu or WinePrefixRuntime.Proton && !compatOverride)
                startInfo.Environment["STEAM_COMPAT_DATA_PATH"] = runtime == WinePrefixRuntime.Umu && wineOverride
                    ? startInfo.Environment["WINEPREFIX"]
                    : configured.CompatRoot;
            if (!wineOverride)
            {
                startInfo.Environment["WINEPREFIX"] = runtime switch
                {
                    WinePrefixRuntime.Umu => configured.CompatRoot,
                    WinePrefixRuntime.Proton when compatOverride &&
                        startInfo.Environment["STEAM_COMPAT_DATA_PATH"] is { Length: > 0 } root => Path.Combine(root, "pfx"),
                    // Keep the historic input for flat Proton prefixes. The actual
                    // Windows tree is still the one selected by the Proton script.
                    WinePrefixRuntime.Proton when configured.WouldCreateSecondTree &&
                        !Directory.Exists(configured.WindowsPrefix) => configured.CompatRoot,
                    _ => configured.WindowsPrefix
                };
            }
        }

        var overridden = runtime == WinePrefixRuntime.Proton ? compatOverride : wineOverride;
        var prefix = !overridden && configured != null
            ? configured with { RuntimeUncertain = runtimeUncertain }
            : ObserveLaunch(startInfo, runtime, overridden, runtimeUncertain);
        if (prefix == null)
            return null;

        if (runtime == WinePrefixRuntime.Proton && wineOverride)
            prefix = CheckForConflictingOverrides(prefix, startInfo.Environment["WINEPREFIX"]);
        if (runtime == WinePrefixRuntime.Umu && compatOverride)
            prefix = CheckForConflictingOverrides(prefix, startInfo.Environment["STEAM_COMPAT_DATA_PATH"]);

        if (scaffold && applyManagedPrefix && prefix.CanPrepare && !prefix.FromOverride)
        {
            ScaffoldPrefix(prefix);
            if (pathToSave != null)
                item.PrefixPath = pathToSave;
        }
        return prefix;
    }

    internal static PrefixResolution CheckForConflictingOverrides(PrefixResolution prefix, string? secondary)
    {
        var expected = prefix.Runtime == WinePrefixRuntime.Proton ? prefix.WindowsPrefix : prefix.CompatRoot;
        var expectedTarget = ResolveDirectory(expected).Path;
        var conflicting = string.IsNullOrWhiteSpace(secondary) || !Path.IsPathFullyQualified(secondary) ||
                          expectedTarget == null || !FileSystemPathIdentity.Equals(expectedTarget, ResolveDirectory(secondary).Path);
        return prefix with { ConflictingOverrides = conflicting };
    }

    internal static PrefixResolution ResolveConfigured(string prefixPath, WinePrefixRuntime runtime,
        bool useProtonWine = false)
    {
        var explicitPfx = PrefixPathHelper.IsPfxPath(prefixPath);
        var root = explicitPfx ? Path.GetDirectoryName(prefixPath) ?? prefixPath : prefixPath;
        var nested = Path.Combine(root, "pfx");
        var nestedTarget = ResolveDirectory(nested);
        var rootState = GetTreeState(ResolveDirectory(root));
        var target = runtime switch
        {
            WinePrefixRuntime.Umu => nestedTarget.Exists && ReadLink(nested) == null ? nested : root,
            WinePrefixRuntime.Proton when useProtonWine && explicitPfx => prefixPath,
            WinePrefixRuntime.Proton when useProtonWine && !explicitPfx &&
                !nestedTarget.Exists && rootState != PrefixTreeState.Missing => root,
            WinePrefixRuntime.Proton => nested,
            _ when explicitPfx => prefixPath,
            _ => rootState == PrefixTreeState.Missing && nestedTarget.Exists ? nested : root
        };
        return Inspect(root, target, runtime) with
        {
            ExplicitWindowsTree = explicitPfx && (runtime == WinePrefixRuntime.Wine || useProtonWine)
        };
    }

    private void ScaffoldPrefix(PrefixResolution prefix)
    {
        var target = prefix.ResolvedWindowsPrefix!;
        Directory.CreateDirectory(target);

        var dosDevicesDirectory = Path.Combine(target, "dosdevices");
        Directory.CreateDirectory(dosDevicesDirectory);

        // With a new UMU root, umu-run will create the pfx alias and initialize Wine.
        var newUmuRoot = prefix.Runtime == WinePrefixRuntime.Umu &&
                         prefix.State == PrefixTreeState.Missing && prefix.WindowsPrefix == prefix.CompatRoot;
        if (!newUmuRoot)
        {
            Directory.CreateDirectory(Path.Combine(target, "drive_c"));
            PrefixPathHelper.EnsureDosDeviceMapping(dosDevicesDirectory, "c:", "../drive_c");
        }

        if (IsInsideLibrary(prefix, _libraryRootPath))
            PrefixPathHelper.EnsurePortableGamesDriveMapping(prefix.CompatRoot, dosDevicesDirectory, _libraryRootPath);
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

}
