using System;
using System.IO;
using System.Linq;
using Retromind.Helpers;

namespace Retromind.Services;

public sealed partial class WinePrefixService
{
    private const string PortableProfileMarker = ".retromind-prefix-v2";
    internal const string PortableProfileUser = "steamuser";
    internal const string PortableProfileConflictMessage =
        "The portable Windows profile is incomplete or conflicts with the selected prefix. Check the profile and prefix paths.";

    private static readonly string[] WineHomeMappedFolders =
        ["Documents", "Desktop", "Downloads", "Pictures", "Music", "Videos"];

    private static readonly string[] PortableProfileFolders =
    [
        .. WineHomeMappedFolders, "Saved Games", "AppData", "AppData/Roaming", "AppData/Local", "AppData/LocalLow"
    ];

    internal static PrefixResolution ResolveManaged(string prefixPath, WinePrefixRuntime runtime, string libraryRoot,
        bool useProtonWine = false, bool allowNewPortableProfile = true)
    {
        var prefix = ResolveConfigured(prefixPath, runtime, useProtonWine);
        if (!allowNewPortableProfile || !OperatingSystem.IsLinux() ||
            EntryExists(prefix.CompatRoot) || !IsInsideLibrary(prefix, libraryRoot) ||
            !PrefixPathHelper.TryMakeLibraryRelativeIfInsideLibraryRoot(prefix.CompatRoot, libraryRoot, out var relative) ||
            relative == ".")
            return prefix;

        var nested = Inspect(prefix.CompatRoot, Path.Combine(prefix.CompatRoot, "pfx"), runtime);
        return nested with { PortableProfile = true, CreatePortableProfile = true };
    }

    internal static void PreparePortableProfile(PrefixResolution prefix, string libraryRoot)
    {
        if (!prefix.PortableProfile)
            return;

        if (!prefix.CreatePortableProfile || EntryExists(prefix.CompatRoot))
        {
            RequireExistingProfile(prefix);
            return;
        }

        var currentRoot = ResolveDirectory(prefix.CompatRoot);
        if (currentRoot is not { Path: { } root, BrokenLink: false } ||
            !FileSystemPathIdentity.Equals(Path.Combine(root, "pfx"), prefix.ResolvedWindowsPrefix) ||
            !IsInsideLibrary(prefix, libraryRoot))
            throw new InvalidOperationException(PortableProfileConflictMessage);

        var parent = Path.GetDirectoryName(root)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".retromind-prefix-{Guid.NewGuid():N}");
        try
        {
            // Publish the marker with the complete directory structure. A retry can use
            // a published prefix, and a conflicting destination is never adopted as V2.
            var pfx = Path.Combine(staging, "pfx");
            var users = Path.Combine(pfx, "drive_c", "users");
            CreateProfileDirectories(Path.Combine(users, PortableProfileUser), PortableProfileFolders);
            CreateProfileDirectories(Path.Combine(users, "Default"), WineHomeMappedFolders);

            var dosDevices = Path.Combine(pfx, "dosdevices");
            PrefixPathHelper.EnsureDosDeviceMapping(dosDevices, "c:", "../drive_c");
            PrefixPathHelper.EnsureDosDeviceMapping(dosDevices, "z:", "/");
            if (!Directory.Exists(Path.Combine(dosDevices, "c:")) || !Directory.Exists(Path.Combine(dosDevices, "z:")))
                throw new IOException("Could not prepare the standard Wine drives C: and Z:.");

            File.WriteAllText(Path.Combine(staging, PortableProfileMarker), string.Empty);
            try
            {
                Directory.Move(staging, root);
            }
            catch (IOException) when (EntryExists(root))
            {
                RequireExistingProfile(prefix);
            }
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    private static void CreateProfileDirectories(string profile, string[] folders)
    {
        foreach (var folder in folders)
            Directory.CreateDirectory(Path.Combine(profile, folder));

        // Wine links these folders to the host when initializing the active and default profiles.
        // Templates is only prepared at creation time, so older V2 prefixes keep working as-is.
        Directory.CreateDirectory(Path.Combine(profile, "AppData", "Roaming", "Microsoft", "Windows", "Templates"));
    }

    private static void RequireExistingProfile(PrefixResolution prefix)
    {
        var current = Inspect(prefix.CompatRoot, prefix.WindowsPrefix, prefix.Runtime);
        if (!current.PortableProfile || current.ProfileConflict ||
            !FileSystemPathIdentity.Equals(current.ResolvedWindowsPrefix, prefix.ResolvedWindowsPrefix))
            throw new InvalidOperationException(PortableProfileConflictMessage);
    }

    private static bool HasPortableProfileMarker(string root)
    {
        var marker = Path.Combine(root, PortableProfileMarker);
        return File.Exists(marker) && ReadLink(marker) == null;
    }

    private static bool IsPortableWindowsTree(string? path) =>
        path != null && Path.GetFileName(path) == "pfx" && Path.GetDirectoryName(path) is { } parent &&
        HasPortableProfileMarker(parent);

    private static bool HasPortableProfileDirectories(string pfx)
    {
        var drive = Path.Combine(pfx, "drive_c");
        var users = Path.Combine(drive, "users");
        var profile = Path.Combine(users, PortableProfileUser);
        return IsRealDirectory(pfx) && IsRealDirectory(drive) && IsRealDirectory(users) &&
               IsRealDirectory(profile) && IsRealDirectory(Path.Combine(pfx, "dosdevices")) &&
               PortableProfileFolders.All(folder => IsRealDirectory(Path.Combine(profile, folder)));
    }

    private static bool IsRealDirectory(string path) => Directory.Exists(path) && ReadLink(path) == null;

    private static bool EntryExists(string path) => Directory.Exists(path) || File.Exists(path) || ReadLink(path) != null;
}
