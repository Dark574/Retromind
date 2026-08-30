using System.IO;

namespace Retromind.Helpers;

internal static class LinuxFileSystemHelper
{
    internal static void EnsureExecutableBitBestEffort(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return;
        }

        try
        {
            var currentMode = File.GetUnixFileMode(filePath);
            var executableMode = currentMode |
                                 UnixFileMode.UserExecute |
                                 UnixFileMode.GroupExecute |
                                 UnixFileMode.OtherExecute;

            if (executableMode != currentMode)
                File.SetUnixFileMode(filePath, executableMode);
        }
        catch
        {
            // Best-effort: launch/install workflows must not fail solely on this adjustment.
        }
    }
}
