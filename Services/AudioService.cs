using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Retromind.Services;

/// <summary>
/// Service to handle background music playback using an external player (ffplay).
/// Supports formats like mp3, ogg, flac, wav, and sid (C64).
/// </summary>
public class AudioService
{
    private Process? _currentProcess;
    
    // Lock object to ensure thread safety when starting/stopping processes
    private readonly object _processLock = new();

    private const string PlayerExecutable = "ffplay";

    /// <summary>
    /// Stops currently playing music and starts playback of the specified file.
    /// </summary>
    /// <param name="filePath">Full path to the audio file.</param>
    public async Task PlayMusicAsync(string filePath)
    {
        // Stop existing music immediately on the calling thread to prevent overlap
        StopMusic(); 

        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

        // Run the process creation in a background task to keep UI responsive
        await Task.Run(() =>
        {
            lock (_processLock)
            {
                // Double-check if stop was called in the meantime
                StopMusicInternal(); 

                try
                {
                    // ffplay arguments explanation:
                    // -nodisp:     Disable graphical display (audio only).
                    // -autoexit:   Close player when the track finishes.
                    // -loop 0:     Loop infinitely.
                    // -loglevel quiet: Suppress console output to improve performance.
                    var args = $"-nodisp -autoexit -loop 0 -loglevel quiet \"{filePath}\"";

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = PlayerExecutable,
                        Arguments = args,
                        RedirectStandardOutput = false, // Prevent deadlocks by not redirecting
                        RedirectStandardError = false,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    _currentProcess = Process.Start(startInfo);
                }
                catch (Exception ex)
                {
                    // Use Debug.WriteLine so it appears in the IDE output window
                    Debug.WriteLine($"[AudioService] Failed to start playback (is ffplay installed?): {ex.Message}");
                }
            }
        });
    }

    /// <summary>
    /// Stops the currently playing music process if it exists.
    /// </summary>
    public void StopMusic()
    {
        lock (_processLock)
        {
            StopMusicInternal();
        }
    }

    // Internal helper to avoid recursive locking issues if needed later,
    // and to keep the logic DRY (Don't Repeat Yourself).
    private void StopMusicInternal()
    {
        if (_currentProcess != null && !_currentProcess.HasExited)
        {
            try
            {
                _currentProcess.Kill(); // Forcefully kill the player
                _currentProcess.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioService] Error stopping process: {ex.Message}");
            }
            finally
            {
                _currentProcess = null;
            }
        }
    }
}