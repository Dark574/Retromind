using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Retromind.Services;

public class AudioService
{
    private Process? _currentProcess;

    public async void PlayMusic(string filePath)
    {
        StopMusic(); // Altes stoppen

        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

        // Wir starten das Abspielen in einem Task, damit das UI nicht blockiert
        await Task.Run(() =>
        {
            try
            {
                // Auf Linux ist 'mpv' oder 'ffplay' super.
                // 'ffplay -nodisp -autoexit -loop 0' spielt Audio ohne Fenster im Loop.
                // Wir probieren ffplay, da oft vorhanden. Alternativ 'aplay' für wav.

                var startInfo = new ProcessStartInfo
                {
                    FileName = "ffplay",
                    // -loglevel quiet unterdrückt Ausgaben, das hilft oft zusätzlich
                    Arguments = $"-nodisp -autoexit -loop 0 -loglevel quiet \"{filePath}\"",

                    // Wir leiten den Output NICHT mehr um, um Deadlocks zu vermeiden
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,

                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                _currentProcess = Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Konnte Audio nicht starten (ffplay installiert?): {ex.Message}");
            }
        });
    }

    public void StopMusic()
    {
        if (_currentProcess != null && !_currentProcess.HasExited)
            try
            {
                _currentProcess.Kill(); // Prozess hart beenden
                _currentProcess.Dispose();
            }
            catch
            {
                // Ignorieren
            }
            finally
            {
                _currentProcess = null;
            }
    }
}