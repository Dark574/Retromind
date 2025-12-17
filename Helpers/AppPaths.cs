using System;
using System.IO;

namespace Retromind.Helpers;

public static class AppPaths
{
    /// <summary>
    /// Writable "portable" root for app data (Library, JSON files, etc.).
    /// - AppImage: directory containing the AppImage file (ENV: APPIMAGE)
    /// - Fallback: AppContext.BaseDirectory (normal publish/run)
    /// </summary>
    public static string DataRoot
    {
        get
        {
            var appImagePath = Environment.GetEnvironmentVariable("APPIMAGE");
            if (!string.IsNullOrWhiteSpace(appImagePath))
            {
                var dir = Path.GetDirectoryName(appImagePath);
                if (!string.IsNullOrWhiteSpace(dir))
                    return dir;
            }

            return AppContext.BaseDirectory;
        }
    }

    public static string LibraryRoot => Path.Combine(DataRoot, "Library");
}