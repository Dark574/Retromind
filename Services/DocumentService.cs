using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Retromind.Services;

/// <summary>
/// Linux-first implementation of <see cref="IDocumentService"/> using xdg-open
/// to delegate document viewing to the desktop environment.
/// 
/// This works inside AppImage bundles as long as xdg-open is available on the host.
/// </summary>
public sealed class DocumentService : IDocumentService
{
    /// <inheritdoc />
    public void OpenDocument(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return;

        // Best-effort: only attempt to open existing files
        if (!File.Exists(fullPath))
            return;

        try
        {
            // On Linux, xdg-open is the standard way to ask the desktop environment
            // to open a file or URL with the user's preferred application.
            //
            // Example:
            //   xdg-open "/path/to/manual.pdf"
            //
            // We avoid shell=true to keep argument handling predictable.
            var psi = new ProcessStartInfo
            {
                FileName = "xdg-open",
                ArgumentList = { fullPath },
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            SanitizeEnvironmentForHostProcess(psi);

            var process = Process.Start(psi);
            process?.Dispose();
        }
        catch (Exception ex)
        {
            // Best-effort: document opening must never crash the UI.
            // For now we just log to Debug. In the future this could be
            // surfaced as a non-blocking notification/toast
            Debug.WriteLine($"[DocumentService] Failed to open document '{fullPath}': {ex.Message}");
        }
    }

    /// <summary>
    /// External host tools (xdg-open/kde-open) must not inherit AppImage library overrides.
    /// Otherwise host binaries can load bundled OpenSSL/libcurl versions and fail at startup.
    /// </summary>
    private static void SanitizeEnvironmentForHostProcess(ProcessStartInfo psi)
    {
        // Avoid AppImage-shipped libs shadowing host libs for desktop helpers.
        psi.Environment.Remove("LD_LIBRARY_PATH");
        psi.Environment.Remove("VLC_PLUGIN_PATH");

        // Also remove AppImage-injected PATH segments (APPDIR/usr/bin) when possible
        // so host helper binaries resolve from the system.
        if (!psi.Environment.TryGetValue("PATH", out var pathValue) ||
            string.IsNullOrWhiteSpace(pathValue))
        {
            return;
        }

        var appDir = Environment.GetEnvironmentVariable("APPDIR");
        if (string.IsNullOrWhiteSpace(appDir))
            return;

        var separator = Path.PathSeparator;
        var filteredSegments = pathValue
            .Split(separator, StringSplitOptions.RemoveEmptyEntries)
            .Where(segment =>
                !segment.Equals(appDir, StringComparison.Ordinal) &&
                !segment.StartsWith(appDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToArray();

        if (filteredSegments.Length == 0)
            return;

        psi.Environment["PATH"] = string.Join(separator, filteredSegments);
    }
}
