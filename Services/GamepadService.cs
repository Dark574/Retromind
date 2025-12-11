using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Retromind.Services;

public class GamepadService : IDisposable
{
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly List<Task> _readTasks = new();

    // Events
    public event Action? OnUp;
    public event Action? OnDown;
    public event Action? OnLeft;
    public event Action? OnRight;
    public event Action? OnSelect; // A / Start
    public event Action? OnBack;   // B / Select

    public void Initialize()
    {
        Stop(); 

        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;

        // Suche ALLE Joysticks und starte für JEDEN einen Listener
        var devices = Directory.GetFiles("/dev/input/", "js*");
        
        if (devices.Length == 0)
        {
            Console.WriteLine("[Gamepad] ⚠️ KEIN Joystick unter /dev/input/js* gefunden!");
            return;
        }

        foreach (var device in devices)
        {
            Console.WriteLine($"[Gamepad] Starte Listener für: {device}");
            var task = Task.Run(() => ReadJoystickLoop(device, token), token);
            _readTasks.Add(task);
        }
    }

    private void ReadJoystickLoop(string devicePath, CancellationToken token)
    {
        try
        {
            using (var fs = new FileStream(devicePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] buffer = new byte[8];

                while (!token.IsCancellationRequested)
                {
                    int bytesRead = fs.Read(buffer, 0, 8);
                    if (bytesRead < 8) break; 

                    short value = BitConverter.ToInt16(buffer, 4);
                    byte type = buffer[6];
                    byte number = buffer[7];

                    byte realType = (byte)(type & ~0x80);

                    if (realType == 0x01) // BUTTON
                    {
                        HandleButton(number, value > 0);
                    }
                    else if (realType == 0x02) // AXIS
                    {
                        HandleAxis(number, value);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Ein Fehler bei EINEM Device (z.B. Maus abgezogen) soll die anderen nicht stören
            Console.WriteLine($"[Gamepad] Fehler bei {devicePath}: {ex.Message}");
        }
    }

    // --- MAPPING (Standard Xbox 360 / 8BitDo XInput) ---

    // Wir brauchen separate Achsen-States pro Aufruf, aber da wir hier
    // aus verschiedenen Threads feuern KÖNNTEN, ist ein einfacher statischer State riskant.
    // Aber für Single-User Szenario ist es okay, wenn wir einfach globale States nutzen,
    // solange nicht zwei Controller gleichzeitig gedrückt werden.
    
    private int _lastAxisX = 0;
    private int _lastAxisY = 0;

    private void HandleAxis(byte axis, short value)
    {
        // Debugging einkommentieren, falls nötig
        // if (Math.Abs(value) > 1000) Console.WriteLine($"[AXIS] {axis}: {value}");

        const short Deadzone = 15000; 

        // X-Achse (Links/Rechts)
        if (axis == 0 || axis == 6) 
        {
            int direction = 0;
            if (value < -Deadzone) direction = -1; 
            else if (value > Deadzone) direction = 1; 

            if (direction != _lastAxisX)
            {
                // Um Thread-Safety bei Events zu garantieren, locken wir kurz (optional)
                if (direction == -1) OnLeft?.Invoke(); 
                if (direction == 1)  OnRight?.Invoke(); 
                _lastAxisX = direction;
            }
        }
        // Y-Achse (Oben/Unten)
        else if (axis == 1 || axis == 7)
        {
            int direction = 0;
            if (value < -Deadzone) direction = -1; 
            else if (value > Deadzone) direction = 1; 

            if (direction != _lastAxisY)
            {
                if (direction == -1) OnUp?.Invoke(); 
                if (direction == 1)  OnDown?.Invoke(); 
                _lastAxisY = direction;
            }
        }
    }

    private void HandleButton(byte button, bool pressed)
    {
        if (!pressed) return; 

        Console.WriteLine($"[Gamepad] Button {button} pressed");

        // Standard Xbox Mapping unter Linux
        switch (button)
        {
            case 0: // A
                OnSelect?.Invoke();
                break;
            case 1: // B
                OnBack?.Invoke();
                break;
            case 3: // X
            case 4: // Y
                 // Optional
                break;
            case 6: // Back / Select
                OnBack?.Invoke();
                break;
            case 7: // Start
                OnSelect?.Invoke();
                break;
        }
    }

    private void Stop()
    {
        _cancellationTokenSource?.Cancel();
        // Wir warten nicht auf die Tasks, da Read() blockiert und sich schwer canceln lässt
        _readTasks.Clear();
    }

    public void Dispose()
    {
        Stop();
    }
}