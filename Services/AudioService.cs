using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Retromind.Services;

/// <summary>
/// Service responsible for handling background music playback using an external player (ffplay).
/// Supports a wide range of formats including mp3, ogg, flac, wav, and chiptunes (sid, nsf).
/// </summary>
public class AudioService
{
    private const string PlayerExecutable = "ffplay";
    
    // Default volume (0-100). ffplay accepts values > 100 for amplification, but we stick to standard range.
    private const int DefaultVolume = 70;

    private Process? _currentProcess;
    
    // Guard to prevent concurrent playback start/stop operations
    private readonly object _processLock = new();

    // Token source to cancel pending playback requests if the user switches tracks quickly
    private CancellationTokenSource? _playbackCts;

    /// <summary>
    /// Stops currently playing music and starts playback of the specified file.
    /// Handles rapid track switching gracefully.
    /// </summary>
    /// <param name="filePath">Full path to the audio file.</param>
    public async Task PlayMusicAsync(string filePath)
    {
        // 1. Cancel any pending playback task that hasn't started the process yet
        lock (_processLock)
        {
            _playbackCts?.Cancel();
            _playbackCts?.Dispose();
            _playbackCts = new CancellationTokenSource();
            
            // Immediately stop current music to make UI feel responsive
            StopMusicInternal();
        }

        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

        var token = _playbackCts.Token;

        // 2. Offload process creation to background thread
        await Task.Run(() =>
        {
            if (token.IsCancellationRequested) return;

            lock (_processLock)
            {
                // Double-check cancellation after acquiring lock
                if (token.IsCancellationRequested) return;

                // Ensure clean state again (paranoid check against race conditions)
                StopMusicInternal();

                try
                {
                    var startInfo = new ProcessStartInfo
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        // Redirect stdout/stderr so player logs don't pollute the host terminal.
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    // Simple SID detection by extension
                    var isSid = filePath.EndsWith(".sid", StringComparison.OrdinalIgnoreCase);

                    if (isSid)
                    {
                        startInfo.FileName = "sidplayfp";
                        startInfo.ArgumentList.Clear();

                        // Quiet mode (-q) to minimize console/log noise
                        startInfo.ArgumentList.Add("-q");
                        
                        // SID file path
                        startInfo.ArgumentList.Add(filePath);
                        
                        Debug.WriteLine($"[AudioService] Playing SID via sidplayfp: {filePath}");
                    }
                    else
                    {
                        // Default path: ffplay for "normal" audio formats (mp3, ogg, flac, wav, ...)
                        startInfo.FileName = PlayerExecutable;

                        // Construct arguments using the safer ArgumentList
                        startInfo.ArgumentList.Add("-nodisp");       // No graphical window
                        startInfo.ArgumentList.Add("-autoexit");     // Close when done
                        startInfo.ArgumentList.Add("-loop");         // Loop count
                        startInfo.ArgumentList.Add("0");             // 0 = infinite
                        startInfo.ArgumentList.Add("-loglevel");     // Log level
                        startInfo.ArgumentList.Add("quiet");         // Suppress output
                        startInfo.ArgumentList.Add("-volume");       // Volume control
                        startInfo.ArgumentList.Add(DefaultVolume.ToString()); 
                        
                        // The file path is added last
                        startInfo.ArgumentList.Add(filePath);

                        Debug.WriteLine($"[AudioService] Playing via ffplay: {filePath}");
                    }

                    _currentProcess = Process.Start(startInfo);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AudioService] Failed to start audio player: {ex.Message}");
                    Debug.WriteLine("[AudioService] Ensure ffmpeg/ffplay (and sidplayfp for SID files) is installed and in your PATH.");
                }
            }
        }, token);
    }

    /// <summary>
    /// Stops the currently playing music process if it exists.
    /// </summary>
    public void StopMusic()
    {
        lock (_processLock)
        {
            _playbackCts?.Cancel(); // Cancel pending starts
            _playbackCts?.Dispose();
            _playbackCts = null;
            StopMusicInternal();
        }
    }

    /// <summary>
    /// Internal helper to kill the process. Must be called within a lock.
    /// </summary>
    private void StopMusicInternal()
    {
        if (_currentProcess == null) return;

        try
        {
            if (!_currentProcess.HasExited)
            {
                _currentProcess.Kill();
            }

            // Best-effort wait to avoid leaving a zombie process on Linux.
            _currentProcess.WaitForExit(500);
        }
        catch (Exception ex)
        {
            // Process might have exited between check and kill, or access denied.
            // Usually safe to ignore during cleanup.
            Debug.WriteLine($"[AudioService] Warning during stop: {ex.Message}");
        }
        finally
        {
            _currentProcess.Dispose();
            _currentProcess = null;
        }
    }
}
