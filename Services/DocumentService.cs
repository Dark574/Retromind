using System;
using System.Diagnostics;
using System.IO;

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
}
