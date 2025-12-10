using System;
using System.Linq;
using Silk.NET.Input;

namespace Retromind.Services;

public class GamepadService : IDisposable
{
    private IInputContext? _inputContext;
    
    public event Action? OnUp;
    public event Action? OnDown;
    public event Action? OnLeft;
    public event Action? OnRight;
    public event Action? OnSelect; 
    public event Action? OnBack;   
    
    private DateTime _lastInputTime = DateTime.MinValue;
    private const int InputDelayMs = 150; // Debounce Zeit

    // Zustand für Achsen-Simulation (damit wir nicht dauerfeuern)
    private bool _axisUpActive = false;
    private bool _axisDownActive = false;
    private bool _axisLeftActive = false;
    private bool _axisRightActive = false;

    public void Initialize()
    {
        try
        {
            _inputContext = Windowing.CreateInput();
            _inputContext.ConnectionChanged += OnConnectionChanged;
            
            // Debugging: Was sehen wir wirklich?
            System.Diagnostics.Debug.WriteLine($"[Input] Found {_inputContext.Gamepads.Count} Gamepads");
            System.Diagnostics.Debug.WriteLine($"[Input] Found {_inputContext.Joysticks.Count} Joysticks");

            foreach (var g in _inputContext.Gamepads) 
            {
                System.Diagnostics.Debug.WriteLine($"[Input] GP: {g.Name}");
                g.ButtonDown += OnGamepadButtonDown;
            }
            
            foreach (var j in _inputContext.Joysticks) 
            {
                System.Diagnostics.Debug.WriteLine($"[Input] Joy: {j.Name}");
                j.ButtonDown += OnJoystickButtonDown;
                j.HatMoved += OnJoystickHatMoved;
                j.AxisMoved += OnJoystickAxisMoved; // <-- NEU: Achsen für D-Pad Support
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Gamepad Init Failed: {ex.Message}");
        }
    }

    private void OnConnectionChanged(IInputDevice device, bool connected)
    {
        System.Diagnostics.Debug.WriteLine($"[Input] Connection changed: {device.Name} -> {connected}");
        
        if (device is IGamepad g)
        {
            if (connected) g.ButtonDown += OnGamepadButtonDown;
            else g.ButtonDown -= OnGamepadButtonDown;
        }
        else if (device is IJoystick j)
        {
            if (connected)
            {
                j.ButtonDown += OnJoystickButtonDown;
                j.HatMoved += OnJoystickHatMoved;
                j.AxisMoved += OnJoystickAxisMoved;
            }
            else
            {
                j.ButtonDown -= OnJoystickButtonDown;
                j.HatMoved -= OnJoystickHatMoved;
                j.AxisMoved -= OnJoystickAxisMoved;
            }
        }
    }

    // --- 1. ECHTE GAMEPADS ---
    private void OnGamepadButtonDown(IGamepad gamepad, Button button)
    {
        if (IsDebounced()) return;
        
        // Debug
        // System.Diagnostics.Debug.WriteLine($"GP Btn: {button.Name}");

        switch (button.Name)
        {
            case ButtonName.DPadUp:    OnUp?.Invoke(); break;
            case ButtonName.DPadDown:  OnDown?.Invoke(); break;
            case ButtonName.DPadLeft:  OnLeft?.Invoke(); break;
            case ButtonName.DPadRight: OnRight?.Invoke(); break;
            case ButtonName.A:         OnSelect?.Invoke(); break;
            case ButtonName.B:         OnBack?.Invoke(); break;
        }
    }

    // --- 2. JOYSTICK BUTTONS ---
    private void OnJoystickButtonDown(IJoystick joystick, Button button)
    {
        // Filter für Corsair Maus
        if (joystick.Name.Contains("Corsair", StringComparison.OrdinalIgnoreCase)) return;

        if (IsDebounced()) return;
        
        System.Diagnostics.Debug.WriteLine($"[Joy] '{joystick.Name}' Btn Index: {button.Index}");

        // Häufige Mappings bei SNES USB
        if (button.Index == 1 || button.Index == 2) OnSelect?.Invoke(); // A / B / X / Y probieren
        if (button.Index == 0 || button.Index == 3) OnBack?.Invoke();   
        
        // Start/Select
        if (button.Index == 9) OnSelect?.Invoke();
        if (button.Index == 8) OnBack?.Invoke();
    }

    // --- 3. JOYSTICK HATS (D-Pad als Hat) ---
    private void OnJoystickHatMoved(IJoystick joystick, Hat hat)
    {
        if (joystick.Name.Contains("Corsair", StringComparison.OrdinalIgnoreCase)) return;
        if (IsDebounced()) return;

        // TRICK: Wir nutzen 'var' und casten direkt, ohne den Enum-Typennamen zu nennen.
        // Das behebt den Compiler-Fehler.
        int pos = (int)hat.Position;

        // SDL Hat Werte: Up=1, Right=2, Down=4, Left=8
        if (pos == 1) OnUp?.Invoke();
        else if (pos == 4) OnDown?.Invoke();
        else if (pos == 8) OnLeft?.Invoke();
        else if (pos == 2) OnRight?.Invoke();
    }

    // --- 4. JOYSTICK AXES (D-Pad als Analog-Achse) ---
    // Viele billige Controller senden D-Pad als Axis 0 (X) und Axis 1 (Y)
    private void OnJoystickAxisMoved(IJoystick joystick, Axis axis)
    {
        if (joystick.Name.Contains("Corsair", StringComparison.OrdinalIgnoreCase)) return;

        // Achsen feuern extrem oft Events (Noise). Wir brauchen einen Schwellenwert.
        float threshold = 0.5f;

        // Debug Ausgabe (nur bei starker Bewegung, sonst flutet es die Konsole)
        if (Math.Abs(axis.Position) > 0.8f)
             System.Diagnostics.Debug.WriteLine($"[Joy] Axis {axis.Index} Val: {axis.Position}");

        // X-Achse (Links/Rechts) - Meistens Index 0
        if (axis.Index == 0)
        {
            if (axis.Position < -threshold && !_axisLeftActive)
            {
                _axisLeftActive = true;
                OnLeft?.Invoke();
            }
            else if (axis.Position > threshold && !_axisRightActive)
            {
                _axisRightActive = true;
                OnRight?.Invoke();
            }
            else if (Math.Abs(axis.Position) < threshold)
            {
                _axisLeftActive = false;
                _axisRightActive = false;
            }
        }
        
        // Y-Achse (Oben/Unten) - Meistens Index 1
        if (axis.Index == 1)
        {
            // Achtung: Bei manchen Controllern ist -1 Oben, bei manchen Unten.
            // Standard: -1 = Oben/Links
            
            if (axis.Position < -threshold && !_axisUpActive)
            {
                _axisUpActive = true;
                OnUp?.Invoke();
            }
            else if (axis.Position > threshold && !_axisDownActive)
            {
                _axisDownActive = true;
                OnDown?.Invoke();
            }
            else if (Math.Abs(axis.Position) < threshold)
            {
                _axisUpActive = false;
                _axisDownActive = false;
            }
        }
    }

    private bool IsDebounced()
    {
        if ((DateTime.Now - _lastInputTime).TotalMilliseconds < InputDelayMs) return true;
        _lastInputTime = DateTime.Now;
        return false;
    }

    public void Dispose()
    {
        _inputContext?.Dispose();
    }
}

public static class Windowing
{
    public static IInputContext CreateInput()
    {
        Silk.NET.Windowing.Window.PrioritizeSdl();
        return Silk.NET.Input.InputWindowExtensions.CreateInput(null!); 
    }
}