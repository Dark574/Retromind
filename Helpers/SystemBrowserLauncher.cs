using System;
using System.Diagnostics;

namespace Retromind.Helpers;

public static class SystemBrowserLauncher
{
    public static bool TryOpen(Uri uri, out Exception? error)
    {
        try
        {
            using var process = Process.Start(CreateStartInfo(uri));
            if (process == null)
                throw new InvalidOperationException("Browser process could not be started.");

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri)
            throw new ArgumentException("Browser URI must be absolute.", nameof(uri));

        if (OperatingSystem.IsLinux())
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "xdg-open",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(uri.AbsoluteUri);
            HostProcessEnvironmentSanitizer.Sanitize(startInfo);
            return startInfo;
        }

        return new ProcessStartInfo
        {
            FileName = uri.AbsoluteUri,
            UseShellExecute = true
        };
    }
}
