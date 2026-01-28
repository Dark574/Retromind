using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Retromind.Services;

/// <summary>
/// A service for playing short, "fire-and-forget" sound effects using ffplay.
/// Each sound is launched in its own process, allowing for multiple overlapping sounds.
/// </summary>
public class SoundEffectService
{
    private const string PlayerExecutable = "ffplay";
    private const int DefaultVolume = 80; // Sound effects can be a bit louder

    /// <summary>
    /// Plays a sound file from the given path without blocking.
    /// Does not loop and the process auto-exits.
    /// </summary>
    public void PlaySound(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            return;
        }

        // Offload to a background thread to ensure the UI is never blocked.
        Task.Run(() =>
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = PlayerExecutable,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = false, // We don't care about output for SFX
                    RedirectStandardError = false
                };

                // Arguments for short sound effects:
                startInfo.ArgumentList.Add("-nodisp");       // No graphical window
                startInfo.ArgumentList.Add("-autoexit");     // Close when done (CRITICAL for SFX)
                startInfo.ArgumentList.Add("-loglevel");     // Log level
                startInfo.ArgumentList.Add("quiet");         // Suppress all output
                startInfo.ArgumentList.Add("-volume");       // Volume control
                startInfo.ArgumentList.Add(DefaultVolume.ToString()); 
                
                // The file path is added last
                startInfo.ArgumentList.Add(filePath);

                // We start the process but don't hold a reference to it.
                // It will live and die on its own. "Fire and forget".
                var process = Process.Start(startInfo);
                process?.Dispose();
            }
            catch (Exception ex)
            {
                // Log this for debugging, but don't crash the app.
                // This usually happens if ffplay is not in the system's PATH.
                Debug.WriteLine($"[SoundEffectService] Failed to play sound '{filePath}': {ex.Message}");
            }
        });
    }
}
