using System;
using System.IO;
using System.Runtime.InteropServices;
using Retromind.Models;

namespace Retromind.Helpers;

internal sealed record AssetRemoval(
    string Path, AssetType Type, long Length, DateTime LastWriteTimeUtc, string? ReplacementPath)
{
    internal static AssetRemoval? Capture(string path, AssetType type, string? replacementPath = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        if (!AppPaths.TryResolveDataPathForMutation(path, out var safePath))
            throw new IOException($"Asset path contains an unsafe symbolic link or escapes the data root: '{path}'.");

        var file = new FileInfo(safePath);
        return file.Exists
            ? new AssetRemoval(safePath, type, file.Length, file.LastWriteTimeUtc, replacementPath)
            : null;
    }
}

internal static partial class AssetRelocation
{
    /// <summary>Stages a new path while keeping the saved library's source path available.</summary>
    internal static void Stage(string source, string destination, bool allowHardLink = true)
    {
        if (!AppPaths.TryResolveDataPathForMutation(source, out source) ||
            !AppPaths.TryResolveDataPathForMutation(destination, out destination))
            throw new IOException("The asset relocation path is unsafe.");

        // Retromind publishes replacement assets as new files. A hard link avoids duplicating
        // large assets during the interval when the old and new library paths both need to work.
        if (!allowHardLink || Link(source, destination) != 0)
            File.Copy(source, destination, overwrite: false);
    }

    [LibraryImport("libc", EntryPoint = "link", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Link(string source, string destination);
}
