using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.SDL;

namespace Retromind.Services;

/// <summary>
/// Handles Gamepad Input using SDL2 via Silk.NET.
/// Provides robust hot-plugging and automatic button mapping.
/// </summary>
public class GamepadService : IDisposable
{
    // Events
    public event Action? OnUp;
    public event Action? OnDown;
    public event Action? OnLeft;
    public event Action? OnRight;
    public event Action? OnSelect; // A / Cross
    public event Action? OnBack;   // B / Circle
    public event Action? OnPrevTab; // L1 / LB
    public event Action? OnNextTab; // R1 / RB

    private readonly Sdl _sdl;
    private CancellationTokenSource? _monitoringCts;
    
    // Wir speichern offene Controller Pointer. Key ist die InstanceID (von SDL vergeben).
    private readonly Dictionary<int, IntPtr> _activeControllers = new();

    private const int DeadZone = 15000; // SDL Range ist -32768 bis 32767

    public GamepadService()
    {
        // Hole die SDL Instanz
        _sdl = Sdl.GetApi();
    }

    public void StartMonitoring()
    {
        StopMonitoring();
        _monitoringCts = new CancellationTokenSource();
        
        // SDL Init muss passieren. Wir nutzen nur das GameController Subsystem.
        // INIT_GAMECONTROLLER impliziert auch INIT_JOYSTICK.
        if (_sdl.Init(Sdl.InitGamecontroller) < 0)
        {
            // Fehler beim Init (z.B. SDL nicht installiert)
            Console.WriteLine($"[SDL] Init failed: {_sdl.GetErrorS()}");
            return;
        }

        Task.Run(() => GameLoop(_monitoringCts.Token));
    }

    public void StopMonitoring()
    {
        _monitoringCts?.Cancel();
        
        unsafe 
        {
            foreach (var ptr in _activeControllers.Values)
            {
                _sdl.GameControllerClose((GameController*)ptr);
            }
        }
        _activeControllers.Clear();
        
        // SDL herunterfahren
        _sdl.Quit();
    }

    private async Task GameLoop(CancellationToken token)
    {
        Event sdlEvent;
        
        while (!token.IsCancellationRequested)
        {
            // FIX: Wir nutzen eine synchrone Hilfsmethode für den Pointer-Zugriff.
            // Das eliminiert die CS9123 Warnung und macht den Code sauberer.
            while (PollNextEvent(out sdlEvent))
            {
                ProcessEvent(sdlEvent);
            }

            try { await Task.Delay(10, token); } catch { break; }
        }
    }

    // Diese Methode kapselt den unsafe Pointer Zugriff isoliert vom async State-Machine Code
    private unsafe bool PollNextEvent(out Event e)
    {
        Event tempEvent = default;
        int result = _sdl.PollEvent(&tempEvent);
            
        e = tempEvent;
        return result == 1;
    }
    
    private void ProcessEvent(Event sdlEvent)
    {
        switch ((EventType)sdlEvent.Type)
        {
            case EventType.Controllerdeviceadded:
                HandleDeviceAdded(sdlEvent.Cdevice);
                break;
            
            case EventType.Controllerdeviceremoved:
                HandleDeviceRemoved(sdlEvent.Cdevice);
                break;
            
            case EventType.Controllerbuttondown:
                HandleButton(sdlEvent.Cbutton);
                break;
            
            case EventType.Controlleraxismotion:
                HandleAxis(sdlEvent.Caxis);
                break;
        }
    }
    
    private unsafe void HandleDeviceAdded(ControllerDeviceEvent e)
    {
        // 'which' ist hier der Index des Geräts im System
        int index = e.Which;
        
        if (_sdl.IsGameController(index) == SdlBool.True)
        {
            GameController* controller = _sdl.GameControllerOpen(index);
            if (controller != null)
            {
                Joystick* joy = _sdl.GameControllerGetJoystick(controller);
                int instanceId = _sdl.JoystickInstanceID(joy);
            
                _activeControllers[instanceId] = (IntPtr)controller;
                Console.WriteLine($"[SDL] Connected: {_sdl.GameControllerNameS(controller)} (ID: {instanceId})");
            }
        }
    }

    private unsafe void HandleDeviceRemoved(ControllerDeviceEvent e)
    {
        // 'which' ist hier die InstanceID
        int instanceId = e.Which;
        
        if (_activeControllers.TryGetValue(instanceId, out IntPtr ptr))
        {
            _sdl.GameControllerClose((GameController*)ptr);
            _activeControllers.Remove(instanceId);
            Console.WriteLine($"[SDL] Disconnected (ID: {instanceId})");
        }
    }

    private void HandleButton(ControllerButtonEvent e)
    {
        // SDL mappt automatisch Buttons (Xbox Layout Standard)
        var btn = (GameControllerButton)e.Button;
        
        switch (btn)
        {
            case GameControllerButton.A:
                OnSelect?.Invoke();
                break;
            case GameControllerButton.B:
                OnBack?.Invoke();
                break;
            case GameControllerButton.DpadUp:
                OnUp?.Invoke();
                break;
            case GameControllerButton.DpadDown:
                OnDown?.Invoke();
                break;
            case GameControllerButton.DpadLeft:
                OnLeft?.Invoke();
                break;
            case GameControllerButton.DpadRight:
                OnRight?.Invoke();
                break;
            case GameControllerButton.Leftshoulder:
                OnPrevTab?.Invoke();
                break;
            case GameControllerButton.Rightshoulder:
                OnNextTab?.Invoke();
                break;
        }
    }

    // Merker für den letzten Achsen-Status, um Mehrfach-Events zu vermeiden (einfaches Debouncing)
    // 0 = Center, 1 = Positive, -1 = Negative
    private int _lastAxisXState = 0;
    private int _lastAxisYState = 0;

    private void HandleAxis(ControllerAxisEvent e)
    {
        var axis = (GameControllerAxis)e.Axis;
        short value = e.Value;

        // X-Achse (Left Stick)
        if (axis == GameControllerAxis.Leftx)
        {
            int newState = 0;
            if (value < -DeadZone) newState = -1;      // Links
            else if (value > DeadZone) newState = 1;   // Rechts
            
            // Nur feuern, wenn sich der Status ändert (z.B. von Center nach Rechts)
            // Das verhindert Spamming, erlaubt aber präzise Menüsteuerung
            if (newState != _lastAxisXState)
            {
                if (newState == -1) OnLeft?.Invoke();
                if (newState == 1) OnRight?.Invoke();
                _lastAxisXState = newState;
            }
        }
        // Y-Achse (Left Stick)
        else if (axis == GameControllerAxis.Lefty)
        {
            int newState = 0;
            if (value < -DeadZone) newState = -1;      // Oben
            else if (value > DeadZone) newState = 1;   // Unten

            if (newState != _lastAxisYState)
            {
                if (newState == -1) OnUp?.Invoke();
                if (newState == 1) OnDown?.Invoke();
                _lastAxisYState = newState;
            }
        }
    }

    public void Dispose()
    {
        StopMonitoring();
    }
}