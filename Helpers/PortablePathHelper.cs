using System;
using System.Collections.Generic;
using System.IO;
using Retromind.Models;

namespace Retromind.Helpers;

public static class PortablePathHelper
{
    public static bool TryMakeDataRelativeIfInsideDataRoot(string absolutePath, out string relativePath)
    {
        relativePath = string.Empty;

        if (string.IsNullOrWhiteSpace(absolutePath))
            return false;

        if (!Path.IsPathRooted(absolutePath))
            return false;

        try
        {
            var dataRoot = Path.GetFullPath(AppPaths.DataRoot);
            var dataRootWithSep = dataRoot.EndsWith(Path.DirectorySeparatorChar)
                ? dataRoot
                : dataRoot + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(absolutePath);

            if (string.Equals(fullPath, dataRoot, StringComparison.Ordinal) ||
                fullPath.StartsWith(dataRootWithSep, StringComparison.Ordinal))
            {
                relativePath = Path.GetRelativePath(dataRoot, fullPath);
                return true;
            }

            return false;
        }
        catch (Exception ex) when (
            ex is ArgumentException ||
            ex is NotSupportedException ||
            ex is PathTooLongException)
        {
            return false;
        }
    }

    public static string? ConvertPathToPortableIfInsideDataRootPreserveEmpty(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        var trimmed = path.Trim();
        if (!Path.IsPathRooted(trimmed))
            return trimmed;

        return TryMakeDataRelativeIfInsideDataRoot(trimmed, out var relativePath)
            ? relativePath
            : trimmed;
    }

    public static void ConvertWrapperPathsToPortable(List<LaunchWrapper>? wrappers)
    {
        if (wrappers is not { Count: > 0 })
            return;

        foreach (var wrapper in wrappers)
        {
            if (wrapper == null || string.IsNullOrWhiteSpace(wrapper.Path))
                continue;

            wrapper.Path = ConvertPathToPortableIfInsideDataRootPreserveEmpty(wrapper.Path) ?? wrapper.Path;
        }
    }
}
