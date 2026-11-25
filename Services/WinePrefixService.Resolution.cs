using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Retromind.Helpers;

namespace Retromind.Services;

internal enum PrefixTreeState { Missing, Incomplete, Initialized }
internal enum PrefixLayout { Root, Nested, RootAlias, CustomAlias, BrokenAlias }

internal sealed record PrefixResolution(
    WinePrefixRuntime Runtime, string CompatRoot, string WindowsPrefix, string? ResolvedWindowsPrefix,
    PrefixLayout Layout, PrefixTreeState State, bool HasSeparateTrees, bool HasExternalTarget,
    bool WouldCreateSecondTree, bool FromOverride = false, bool RuntimeUncertain = false,
    bool ConflictingOverrides = false, bool ExplicitWindowsTree = false, string? PfxLinkTarget = null,
    bool PortableProfile = false, bool CreatePortableProfile = false, bool ProfileConflict = false)
{
    internal bool CanPrepare => ResolvedWindowsPrefix != null && (!HasSeparateTrees || ExplicitWindowsTree) &&
        !WouldCreateSecondTree && !HasExternalTarget && !ConflictingOverrides && !ProfileConflict &&
        !(Layout == PrefixLayout.BrokenAlias && (Runtime != WinePrefixRuntime.Wine || WindowsPrefix != CompatRoot)) &&
        !(Runtime == WinePrefixRuntime.Umu && Layout == PrefixLayout.CustomAlias);

    internal bool CanRepair => CanPrepare && !HasSeparateTrees && !FromOverride &&
        Layout != PrefixLayout.CustomAlias && !RuntimeUncertain;
}

public sealed partial class WinePrefixService
{
    private static readonly string[] WindowsProfileFolders =
    [
        "Documents", "My Documents", "AppData/Roaming", "AppData/Local", "AppData/LocalLow",
        "Saved Games", "Desktop", "Downloads", "Pictures", "Music", "Videos"
    ];

    internal static WinePrefixRuntime? RuntimeForExecutable(string? executable) => Path.GetFileName(executable?.Trim()) switch
    {
        "wine" or "wine64" => WinePrefixRuntime.Wine,
        "proton" => WinePrefixRuntime.Proton,
        "umu-run" => WinePrefixRuntime.Umu,
        _ => null
    };

    internal static PrefixResolution? ObserveLaunch(ProcessStartInfo startInfo, WinePrefixRuntime runtime,
        bool fromOverride, bool runtimeUncertain)
    {
        startInfo.Environment.TryGetValue(runtime == WinePrefixRuntime.Proton
            ? "STEAM_COMPAT_DATA_PATH" : "WINEPREFIX", out var path);
        if (path == null && runtime == WinePrefixRuntime.Wine && !runtimeUncertain)
        {
            startInfo.Environment.TryGetValue("HOME", out var home);
            home ??= EnvironmentPathHelper.TryGetRealUserHomePath();
            if (!string.IsNullOrWhiteSpace(home))
                path = Path.Combine(home, ".wine");
        }
        return ResolveRuntimePath(path, runtime, fromOverride, runtimeUncertain);
    }

    internal static PrefixResolution? ResolveExplicitOverride(WinePrefixRuntime runtime,
        IReadOnlyDictionary<string, string> environment, out bool specified)
    {
        var primary = runtime == WinePrefixRuntime.Proton ? "STEAM_COMPAT_DATA_PATH" : "WINEPREFIX";
        specified = TryReadPrefixOverride(environment, primary, out var path);
        if (!specified)
            return null;

        var resolution = ResolveRuntimePath(EnvironmentPathHelper.NormalizeDataRootPathIfNeeded(primary, path),
            runtime, fromOverride: true);
        var secondary = runtime == WinePrefixRuntime.Proton ? "WINEPREFIX" : "STEAM_COMPAT_DATA_PATH";
        if (resolution != null && runtime != WinePrefixRuntime.Wine &&
            TryReadPrefixOverride(environment, secondary, out var secondaryPath))
            resolution = CheckForConflictingOverrides(resolution,
                EnvironmentPathHelper.NormalizeDataRootPathIfNeeded(secondary, secondaryPath));
        return resolution;
    }

    internal static bool TryReadPrefixOverride(IReadOnlyDictionary<string, string> environment, string key,
        out string? value)
    {
        value = null;
        var found = false;
        foreach (var (name, candidate) in environment)
        {
            if (!string.Equals(name.Trim(), key, StringComparison.Ordinal))
                continue;
            value = candidate;
            found = true;
        }
        return found;
    }

    private static PrefixResolution? ResolveRuntimePath(string? path, WinePrefixRuntime runtime,
        bool fromOverride, bool runtimeUncertain = false)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return null;

        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.Combine(path, "pfx");
        return runtime switch
        {
            WinePrefixRuntime.Umu => Inspect(path, ResolveDirectory(root).Exists && ReadLink(root) == null
                ? root : path, runtime, fromOverride, runtimeUncertain),
            WinePrefixRuntime.Proton => Inspect(path, root, runtime, fromOverride, runtimeUncertain),
            _ => Inspect(PrefixPathHelper.IsPfxPath(path) ? Path.GetDirectoryName(path)! : path,
                path, runtime, fromOverride, runtimeUncertain) with { ExplicitWindowsTree = fromOverride }
        };
    }

    // This operation is intentionally read-only. All callers supply the path that their
    // process will use, which can differ from the spelling stored on the media item.
    internal static PrefixResolution Inspect(string compatRoot, string windowsPrefix, WinePrefixRuntime runtime,
        bool fromOverride = false, bool runtimeUncertain = false)
    {
        var root = ResolveDirectory(compatRoot);
        var nestedPath = Path.Combine(compatRoot, "pfx");
        var nested = ResolveDirectory(nestedPath);
        var windows = ResolveDirectory(windowsPrefix);
        var pfxLinkTarget = root.Path == null ? null : ReadLink(nestedPath);
        var nestedLink = pfxLinkTarget != null;
        var isRootAlias = nestedLink && nested.Exists && FileSystemPathIdentity.Equals(root.Path, nested.Path);
        var rootState = GetTreeState(root);
        var nestedState = GetTreeState(nested);
        var separate = !isRootAlias && rootState != PrefixTreeState.Missing &&
                       nestedState != PrefixTreeState.Missing &&
                       !FileSystemPathIdentity.Equals(root.Path, nested.Path);
        var layout = nestedLink
            ? isRootAlias ? PrefixLayout.RootAlias : nested.Exists ? PrefixLayout.CustomAlias : PrefixLayout.BrokenAlias
            : FileSystemPathIdentity.Equals(windowsPrefix, nestedPath)
                ? PrefixLayout.Nested
                : PrefixLayout.Root;
        var isNestedTarget = FileSystemPathIdentity.Equals(windowsPrefix, nestedPath);
        var externalTarget = nestedLink && nested.Path != null && root.Path != null &&
                             !IsInsideOrEqual(nested.Path, root.Path);
        var wouldCreateSecondTree = isNestedTarget && !isRootAlias && nestedState == PrefixTreeState.Missing &&
                                    rootState != PrefixTreeState.Missing && runtime != WinePrefixRuntime.Wine;
        var markedRoot = root.Path != null && HasPortableProfileMarker(root.Path);
        var selectedPortableTree = IsPortableWindowsTree(windows.Path);
        var compatIsWindowsTree = runtime != WinePrefixRuntime.Wine && IsPortableWindowsTree(root.Path);
        var portable = markedRoot || selectedPortableTree || compatIsWindowsTree;
        var profileConflict = portable && (separate || compatIsWindowsTree ||
            markedRoot && layout != PrefixLayout.Nested ||
            !selectedPortableTree || !HasPortableProfileDirectories(windows.Path!));
        return new PrefixResolution(runtime, compatRoot, windowsPrefix, windows.BrokenLink ? null : windows.Path, layout,
            GetTreeState(windows), separate, externalTarget, wouldCreateSecondTree, fromOverride, runtimeUncertain,
            PfxLinkTarget: pfxLinkTarget, PortableProfile: portable, ProfileConflict: profileConflict);
    }

    internal static IReadOnlyList<string> Describe(PrefixResolution prefix, Func<string, string, string>? localize = null)
    {
        string T(string key, string fallback) => localize?.Invoke("Launch.Prefix." + key, fallback) ?? fallback;
        var layout = prefix.Layout switch
        {
            PrefixLayout.Root => "Root",
            PrefixLayout.Nested => "pfx/",
            PrefixLayout.RootAlias => $"pfx → {prefix.PfxLinkTarget}",
            PrefixLayout.CustomAlias => T("CustomAlias", "Custom pfx link"),
            _ => T("BrokenAlias", "Broken pfx link")
        };
        var state = prefix.State switch
        {
            PrefixTreeState.Initialized => T("Initialized", "registry and Windows directory present"),
            PrefixTreeState.Incomplete => T("Incomplete", "incomplete"),
            _ => T("Missing", "not initialized")
        };
        var lines = new List<string>
        {
            $"{T("CompatRoot", "Compatibility root")}: {prefix.CompatRoot}",
            $"{T("WindowsPrefix", "Windows prefix (planned)")}: {prefix.WindowsPrefix}",
            $"{T("Layout", "Layout")}: {layout} · {state}"
        };
        if (prefix.ResolvedWindowsPrefix is { } resolved &&
            !FileSystemPathIdentity.Equals(resolved, prefix.WindowsPrefix))
            lines.Add($"{T("ResolvedTarget", "Resolved target")}: {resolved}");
        if (prefix.ResolvedWindowsPrefix == null)
            lines.Add(T("UnresolvedTarget", "The Windows prefix link could not be resolved."));
        if (prefix.PfxLinkTarget is { } link && prefix.Layout is PrefixLayout.CustomAlias or PrefixLayout.BrokenAlias)
            lines.Add($"pfx → {link}");
        if (prefix.HasSeparateTrees)
            lines.Add(T("SeparateTrees", "Root and pfx contain separate Windows data."));
        if (prefix.WouldCreateSecondTree)
            lines.Add(T("SecondTree", "This launch would initialize a second Windows tree in pfx."));
        if (prefix.FromOverride)
            lines.Add(T("EnvironmentOverride", "The launch environment overrides the configured prefix."));
        if (prefix.ConflictingOverrides)
            lines.Add(T("ConflictingOverrides", "WINEPREFIX and STEAM_COMPAT_DATA_PATH point to different prefixes."));
        if (prefix.RuntimeUncertain)
            lines.Add(T("RuntimeUncertain", "A custom launcher or wrapper can select a different prefix at runtime."));
        if (prefix.Runtime == WinePrefixRuntime.Umu && prefix.Layout == PrefixLayout.CustomAlias)
            lines.Add(T("UmuAlias", "UMU can replace the custom pfx link."));
        if (prefix.HasExternalTarget)
            lines.Add(T("ExternalTarget", "The pfx link points outside its compatibility root."));
        if (prefix.PortableProfile)
        {
            var tree = prefix.ProfileConflict ? Path.Combine(prefix.CompatRoot, "pfx") : prefix.ResolvedWindowsPrefix!;
            var profile = Path.Combine(tree, "drive_c", "users", PortableProfileUser);
            lines.Add($"{T("PortableProfile", "Portable Windows profile")}: {profile}");
            if (prefix.ProfileConflict)
                lines.Add(T("ProfileConflict", PortableProfileConflictMessage));
            if (!prefix.ProfileConflict && prefix.ResolvedWindowsPrefix is { } windowsPrefix)
            {
                var drives = DescribeDriveLinks(windowsPrefix);
                if (drives.Count > 0)
                {
                    lines.Add(T("DriveMappings", "Windows drive mappings"));
                    lines.AddRange(drives);
                }
            }
        }

        var profileLocations = InspectProfileLocations(prefix.ResolvedWindowsPrefix);
        if (profileLocations.Count > 0)
        {
            lines.Add(T("ProfileLocations", "External or unresolved profile folders (filesystem)") + ":");
            lines.AddRange(profileLocations.Select(location => $"  {location}"));
        }
        return lines;
    }

    private static List<string> DescribeDriveLinks(string windowsPrefix)
    {
        var links = new List<string>();
        var dosDevices = Path.Combine(windowsPrefix, "dosdevices");
        if (!IsRealDirectory(dosDevices))
            return links;

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(dosDevices).Order(StringComparer.Ordinal))
            {
                var drive = Path.GetFileName(entry);
                if (drive is [>= 'a' and <= 'z', ':'] && ReadLink(entry) is { } target)
                    links.Add($"  {char.ToUpperInvariant(drive[0])}: → {target}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Diagnostics remain best-effort.
        }

        return links;
    }

    private static List<string> InspectProfileLocations(string? windowsPrefix)
    {
        var locations = new List<string>();
        if (windowsPrefix == null)
            return locations;

        var users = Path.Combine(windowsPrefix, "drive_c", "users");
        if (ReportLocation(users, "drive_c/users") || !Directory.Exists(users))
            return locations;

        try
        {
            // Bound the work in the editor and never enumerate an external profile directory.
            foreach (var profile in Directory.EnumerateFileSystemEntries(users).Take(32))
            {
                var profileName = Path.GetFileName(profile);
                if (ReportLocation(profile, profileName) || !Directory.Exists(profile))
                    continue;
                foreach (var folder in WindowsProfileFolders)
                    ReportLocation(Path.Combine(profile, folder), $"{profileName}/{folder}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable profile directory must not prevent a launch or a preview.
        }
        return locations;

        bool ReportLocation(string path, string label)
        {
            var directory = ResolveDirectory(path);
            if (directory.Path == null || directory.BrokenLink)
            {
                locations.Add($"{label} → {directory.Path ?? path} [!]");
                return true;
            }
            if (!directory.Exists || IsInsideOrEqual(directory.Path, windowsPrefix))
                return false;

            locations.Add($"{label} → {directory.Path}");
            return true;
        }
    }

    private static PrefixTreeState GetTreeState(DirectoryTarget directory)
    {
        if (!directory.Exists || directory.Path == null)
            return PrefixTreeState.Missing;
        var path = directory.Path;
        var hasRegistry = File.Exists(Path.Combine(path, "system.reg"));
        var hasDrive = Directory.Exists(Path.Combine(path, "drive_c"));
        if (hasRegistry && File.Exists(Path.Combine(path, "user.reg")) &&
            Directory.Exists(Path.Combine(path, "drive_c", "windows")))
            return PrefixTreeState.Initialized;
        return hasRegistry || hasDrive || File.Exists(Path.Combine(path, "user.reg")) ||
               File.Exists(Path.Combine(path, "userdef.reg"))
            ? PrefixTreeState.Incomplete
            : PrefixTreeState.Missing;
    }

    private static bool IsInsideOrEqual(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    internal static bool IsInsideLibrary(PrefixResolution prefix, string libraryRoot)
    {
        var library = ResolveDirectory(libraryRoot).Path;
        return library != null && prefix.ResolvedWindowsPrefix is { } windows &&
               !FileSystemPathIdentity.Equals(windows, library) && IsInsideOrEqual(windows, library);
    }

    internal static bool StillSelectsSameTree(PrefixResolution prefix)
    {
        var input = prefix.Runtime == WinePrefixRuntime.Wine ? prefix.WindowsPrefix : prefix.CompatRoot;
        var current = ResolveRuntimePath(input, prefix.Runtime, prefix.FromOverride);
        return current is { CanPrepare: true } &&
               FileSystemPathIdentity.Equals(prefix.ResolvedWindowsPrefix, current.ResolvedWindowsPrefix);
    }

    private readonly record struct DirectoryTarget(string? Path, bool Exists, bool BrokenLink);

    private static DirectoryTarget ResolveDirectory(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path)!;
            var segments = new LinkedList<string>(Split(path[root.Length..]));
            var current = root;
            var links = 0;
            var brokenLink = false;
            while (segments.First is { } first)
            {
                segments.RemoveFirst();
                var segment = first.Value;
                if (segment == ".")
                    continue;
                if (segment == "..")
                {
                    current = Path.GetDirectoryName(current) ?? root;
                    continue;
                }
                var candidate = Path.Combine(current, segment);
                var link = ReadLink(candidate);
                if (link == null)
                {
                    current = candidate;
                    continue;
                }
                if (++links > 40)
                    return new(null, false, true);
                brokenLink |= !Directory.Exists(candidate);
                if (Path.IsPathRooted(link))
                {
                    root = Path.GetPathRoot(link)!;
                    current = root;
                    link = link[root.Length..];
                }
                foreach (var part in Split(link).Reverse())
                    segments.AddFirst(part);
            }
            return new(current, Directory.Exists(current), brokenLink);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(null, false, false);
        }

        static string[] Split(string value) => value.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
    }

    private static string? ReadLink(string path) => new DirectoryInfo(path).LinkTarget;
}
