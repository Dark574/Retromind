using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services;

/// <summary>
/// Service responsible for handling background music playback using an external player (ffplay).
/// Supports a wide range of formats including mp3, ogg, opus, flac, wav, and SID chiptunes.
/// </summary>
public class AudioService
{
    private const string PlayerExecutable = "ffplay";
    private const int SidPlaybackDurationSeconds = 3 * 60;
    
    // Default volume (0-100). ffplay accepts values > 100 for amplification, but we stick to standard range.
    private const int DefaultVolume = 70;

    private readonly IErrorService _errorService;
    private readonly Func<ProcessStartInfo, IAudioProcess?> _startProcess;
    private IAudioProcess? _currentProcess;
    private string? _currentPlayer;

    // Incrementing playback id to identify the currently active process.
    private int _playbackId;
    private int _currentPlaybackId;
    private int _stopRequestedPlaybackId;
    
    // Guard to prevent concurrent playback start/stop operations
    private readonly object _processLock = new();

    // Token source to cancel pending playback requests if the user switches tracks quickly
    private CancellationTokenSource? _playbackCts;

    public AudioService(IErrorService errorService)
        : this(errorService, startInfo =>
        {
            var process = Process.Start(startInfo);
            return process == null ? null : new AudioProcess(process);
        })
    {
    }

    internal AudioService(IErrorService errorService, Func<ProcessStartInfo, IAudioProcess?> startProcess)
    {
        _errorService = errorService ?? throw new ArgumentNullException(nameof(errorService));
        _startProcess = startProcess ?? throw new ArgumentNullException(nameof(startProcess));
    }

    /// <summary>
    /// Fired when the current track finishes playback naturally.
    /// </summary>
    public event Action<string>? MusicPlaybackEnded;

    /// <summary>
    /// Stops currently playing music and starts playback of the specified file.
    /// Handles rapid track switching gracefully.
    /// </summary>
    /// <param name="filePath">Full path to the audio file.</param>
    public async Task PlayMusicAsync(string filePath)
    {
        var stopErrors = new List<ErrorReport>();
        CancellationToken token;
        // 1. Cancel any pending playback task that hasn't started the process yet
        lock (_processLock)
        {
            _playbackCts?.Cancel();
            _playbackCts?.Dispose();
            _playbackCts = new CancellationTokenSource();
            token = _playbackCts.Token;
            
            // Immediately stop current music to make UI feel responsive
            StopMusicInternal(stopErrors);
        }
        ReportErrors(stopErrors);

        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

        var playbackId = Interlocked.Increment(ref _playbackId);

        // 2. Offload process creation to background thread
        await Task.Run(() =>
        {
            if (token.IsCancellationRequested) return;

            var errors = new List<ErrorReport>();
            IAudioProcess? startedProcess = null;
            var executableToken = filePath.EndsWith(".sid", StringComparison.OrdinalIgnoreCase)
                ? "sidplayfp"
                : PlayerExecutable;
            lock (_processLock)
            {
                // Double-check cancellation after acquiring lock
                if (token.IsCancellationRequested) return;

                // Ensure clean state again (paranoid check against race conditions)
                StopMusicInternal(errors);

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
                    var bundledExecutable = AppImageToolResolver.ResolveBundledExecutable(executableToken);
                    var executablePath = bundledExecutable ?? executableToken;

                    if (isSid)
                    {
                        startInfo.FileName = executablePath;
                        startInfo.ArgumentList.Clear();

                        // Quiet mode (-q) to minimize console/log noise
                        startInfo.ArgumentList.Add("-q");

                        // SID tunes do not carry a reliable duration and may keep the
                        // player process alive after becoming silent. Limit playback to
                        // the preset subtune so the normal track-ended flow can restart
                        // this file or advance to another music asset.
                        startInfo.ArgumentList.Add("-os");
                        startInfo.ArgumentList.Add($"-t{SidPlaybackDurationSeconds}");
                        
                        // SID file path
                        startInfo.ArgumentList.Add(filePath);
                        
                        Debug.WriteLine($"[AudioService] Playing SID via {startInfo.FileName}: {filePath}");
                    }
                    else
                    {
                        // Default path: ffplay for "normal" audio formats (mp3, ogg, flac, wav, ...)
                        startInfo.FileName = executablePath;

                        // Construct arguments using the safer ArgumentList
                        startInfo.ArgumentList.Add("-nodisp");       // No graphical window
                        startInfo.ArgumentList.Add("-autoexit");     // Close when done
                        startInfo.ArgumentList.Add("-loglevel");     // Log level
                        startInfo.ArgumentList.Add("quiet");         // Suppress output
                        startInfo.ArgumentList.Add("-volume");       // Volume control
                        startInfo.ArgumentList.Add(DefaultVolume.ToString()); 
                        
                        // The file path is added last
                        startInfo.ArgumentList.Add(filePath);

                        Debug.WriteLine($"[AudioService] Playing via {startInfo.FileName}: {filePath}");
                    }

                    // Keep AppImage runtime env for bundled tools.
                    // Only sanitize host fallback launches inside AppImage runtime.
                    if (bundledExecutable != null)
                    {
                        AppImageToolResolver.ConfigureBundledToolEnvironment(startInfo, bundledExecutable);
                    }
                    else if (AppImageToolResolver.IsAppImageRuntime())
                    {
                        HostProcessEnvironmentSanitizer.Sanitize(startInfo);
                    }
                    _currentPlayer = executableToken;
                    _currentProcess = _startProcess(startInfo);

                    if (_currentProcess == null)
                    {
                        errors.Add(CreateReport("Audio.StartFailed", ErrorSeverity.Warning,
                            "The audio player could not be started.", "Start", executableToken,
                            filePath: filePath));
                    }
                    else
                    {
                        _currentPlaybackId = playbackId;
                        startedProcess = _currentProcess;
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(CreateReport("Audio.StartFailed", ErrorSeverity.Warning,
                        "The audio player could not be started. Ensure ffplay (or sidplayfp for SID files) is installed and in PATH.",
                        "Start", executableToken, ex, filePath));
                }
            }

            ReportErrors(errors);
            // Even an already-completed process must be observed outside the start lock:
            // its monitor can run synchronously through to diagnostic output.
            if (startedProcess != null)
                _ = MonitorProcessExitAsync(startedProcess, playbackId, filePath, token, executableToken);
        }, token);
    }

    /// <summary>
    /// Stops the currently playing music process if it exists.
    /// </summary>
    public void StopMusic()
    {
        var errors = new List<ErrorReport>();
        lock (_processLock)
        {
            _playbackCts?.Cancel(); // Cancel pending starts
            _playbackCts?.Dispose();
            _playbackCts = null;
            StopMusicInternal(errors);
        }
        ReportErrors(errors);
    }

    /// <summary>
    /// Internal helper to kill the process. Must be called within a lock.
    /// </summary>
    private void StopMusicInternal(List<ErrorReport> errors)
    {
        if (_currentProcess == null) return;

        try
        {
            _stopRequestedPlaybackId = _currentPlaybackId;

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
            errors.Add(CreateReport("Audio.StopFailed", ErrorSeverity.Diagnostic,
                "The audio player could not be stopped cleanly.", "Stop", _currentPlayer, ex));
        }
        finally
        {
            DisposeCurrentProcess(errors);
        }
    }

    private async Task MonitorProcessExitAsync(
        IAudioProcess process, int playbackId, string filePath, CancellationToken token, string player)
    {
        int exitCode;
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            exitCode = process.ExitCode;
        }
        catch
        {
            return;
        }

        var errors = new List<ErrorReport>();
        lock (_processLock)
        {
            if (token.IsCancellationRequested ||
                playbackId != _currentPlaybackId ||
                playbackId == _stopRequestedPlaybackId ||
                !ReferenceEquals(process, _currentProcess))
            {
                return;
            }

            DisposeCurrentProcess(errors);
        }

        ReportErrors(errors);

        if (exitCode != 0)
        {
            ReportErrors([CreateReport("Audio.ProcessFailed", ErrorSeverity.Warning,
                "The audio player exited unexpectedly; playback will not be restarted.",
                "Playback", player, filePath: filePath, exitCode: exitCode)]);
            return;
        }

        MusicPlaybackEnded?.Invoke(filePath);
    }

    // Called only under _processLock. Reporting is deferred until after releasing it.
    private void DisposeCurrentProcess(List<ErrorReport> errors)
    {
        try
        {
            _currentProcess?.Dispose();
        }
        catch (Exception ex)
        {
            errors.Add(CreateReport("Audio.StopFailed", ErrorSeverity.Diagnostic,
                "The audio player could not be cleaned up completely.", "Dispose", _currentPlayer, ex));
        }
        finally
        {
            _currentProcess = null;
        }
    }

    private void ReportErrors(IEnumerable<ErrorReport> errors)
    {
        foreach (var error in errors)
        {
            try
            {
                _errorService.Report(error);
            }
            catch
            {
                // Even a failing error-service implementation must not affect playback cleanup.
            }
        }
    }

    private static ErrorReport CreateReport(
        string code, ErrorSeverity severity, string message, string operation, string? player,
        Exception? exception = null, string? filePath = null, int? exitCode = null)
    {
        var context = new Dictionary<string, string> { ["Operation"] = operation };
        if (player != null)
            context["Player"] = player;
        if (filePath != null)
            context["FilePath"] = filePath;
        if (exitCode.HasValue)
            context["ExitCode"] = exitCode.Value.ToString(CultureInfo.InvariantCulture);

        return new ErrorReport(code, severity, nameof(AudioService), message, exception, Context: context);
    }

    // A narrow process boundary keeps regression tests independent of installed audio players.
    internal interface IAudioProcess : IDisposable
    {
        bool HasExited { get; }
        int ExitCode { get; }
        void Kill();
        bool WaitForExit(int milliseconds);
        Task WaitForExitAsync();
    }

    private sealed class AudioProcess(Process process) : IAudioProcess
    {
        public bool HasExited => process.HasExited;
        public int ExitCode => process.ExitCode;
        public void Kill() => process.Kill();
        public bool WaitForExit(int milliseconds) => process.WaitForExit(milliseconds);
        public Task WaitForExitAsync() => process.WaitForExitAsync();
        public void Dispose() => process.Dispose();
    }
}
