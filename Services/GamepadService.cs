using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.SDL;

namespace Retromind.Services;

/// <summary>
/// Handles gamepad input via SDL2 (Silk.NET).
/// Supports hot-plugging and provides a small set of high-level navigation events.
/// </summary>
public sealed class GamepadService : IDisposable
{
    // Events (raised on the SDL polling thread; subscribers should marshal to UI thread if needed).
    public event Action? OnUp;
    public event Action? OnDown;
    public event Action? OnLeft;
    public event Action? OnRight;
    public event Action? OnSelect;   // A / Cross
    public event Action? OnBack;     // B / Circle
    public event Action? OnGuide;    // Guide / Home
    public event Action? OnPrevTab;  // L1 / LB
    public event Action? OnNextTab;  // R1 / RB

    private readonly Sdl _sdl = Sdl.GetApi();

    private readonly Dictionary<int, IntPtr> _activeControllers = new();

    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    private bool _isInitialized;

    // SDL axis range is roughly -32768..32767; this filters small drift.
    private const int DeadZone = 15000;

    // Some controllers do not report GUIDE reliably; fallback: Start+Back within a short window.
    private DateTime _lastBackPressedUtc = DateTime.MinValue;
    private DateTime _lastStartPressedUtc = DateTime.MinValue;
    private static readonly TimeSpan GuideComboWindow = TimeSpan.FromMilliseconds(350);

    // Simple axis edge detection to prevent event spam.
    // 0 = center, -1 = negative, 1 = positive
    private int _lastAxisXState;
    private int _lastAxisYState;

    public void StartMonitoring()
    {
        StopMonitoring();

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        if (!EnsureInitialized())
        {
            // Initialization failed (e.g. SDL not available).
            return;
        }

        // Ensure controller events are enabled.
        _sdl.GameControllerEventState(Sdl.Enable);

        _loopTask = Task.Run(() => GameLoopAsync(token), token);
    }

    public void StopMonitoring()
    {
        _cts?.Cancel();

        // Best effort: close all controllers.
        unsafe
        {
            foreach (var ptr in _activeControllers.Values)
            {
                try
                {
                    _sdl.GameControllerClose((GameController*)ptr);
                }
                catch
                {
                    // ignore
                }
            }
        }

        _activeControllers.Clear();

        _cts?.Dispose();
        _cts = null;

        _loopTask = null;

        // Important:
        // We intentionally do NOT call SDL_Quit() here.
        // Global Quit can affect other SDL consumers and may cause odd behavior across re-inits.
        // If you ever want full shutdown, expose a separate method that explicitly quits SDL.
    }

    private bool EnsureInitialized()
    {
        if (_isInitialized)
            return true;

        // We only need the GameController subsystem.
        // INIT_GAMECONTROLLER implies INIT_JOYSTICK.
        if (_sdl.Init(Sdl.InitGamecontroller) < 0)
        {
            Debug.WriteLine($"[SDL] Init failed: {_sdl.GetErrorS()}");
            _isInitialized = false;
            return false;
        }

        _isInitialized = true;
        return true;
    }

    private async Task GameLoopAsync(CancellationToken token)
    {
        Event sdlEvent;

        while (!token.IsCancellationRequested)
        {
            while (PollNextEvent(out sdlEvent))
            {
                ProcessEvent(sdlEvent);
            }

            try
            {
                await Task.Delay(10, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // Isolates unsafe pointer access from the async state machine.
    private unsafe bool PollNextEvent(out Event e)
    {
        Event temp = default;
        int result = _sdl.PollEvent(&temp);

        e = temp;
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
                HandleButtonDown(sdlEvent.Cbutton);
                break;

            case EventType.Controlleraxismotion:
                HandleAxisMotion(sdlEvent.Caxis);
                break;
        }
    }

    private unsafe void HandleDeviceAdded(ControllerDeviceEvent e)
    {
        // "Which" is the device index in the system at the time of the event.
        int index = e.Which;

        if (_sdl.IsGameController(index) != SdlBool.True)
            return;

        GameController* controller = _sdl.GameControllerOpen(index);
        if (controller == null)
            return;

        Joystick* joystick = _sdl.GameControllerGetJoystick(controller);
        int instanceId = _sdl.JoystickInstanceID(joystick);

        _activeControllers[instanceId] = (IntPtr)controller;

        var name = _sdl.GameControllerNameS(controller);
        Debug.WriteLine($"[SDL] Controller connected: {name} (ID: {instanceId})");
    }

    private unsafe void HandleDeviceRemoved(ControllerDeviceEvent e)
    {
        // "Which" is the instance ID.
        int instanceId = e.Which;

        if (!_activeControllers.TryGetValue(instanceId, out var ptr))
            return;

        _sdl.GameControllerClose((GameController*)ptr);
        _activeControllers.Remove(instanceId);

        Debug.WriteLine($"[SDL] Controller disconnected (ID: {instanceId})");
    }

    private void HandleButtonDown(ControllerButtonEvent e)
    {
        // SDL maps buttons to a standard Xbox-style layout.
        var button = (GameControllerButton)e.Button;

        switch (button)
        {
            case GameControllerButton.A:
                OnSelect?.Invoke();
                break;

            case GameControllerButton.B:
                OnBack?.Invoke();
                _lastBackPressedUtc = DateTime.UtcNow;

                if (DateTime.UtcNow - _lastStartPressedUtc <= GuideComboWindow)
                    OnGuide?.Invoke();
                break;

            case GameControllerButton.Start:
                _lastStartPressedUtc = DateTime.UtcNow;

                if (DateTime.UtcNow - _lastBackPressedUtc <= GuideComboWindow)
                    OnGuide?.Invoke();
                break;

            case GameControllerButton.Guide:
                OnGuide?.Invoke();
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

    private void HandleAxisMotion(ControllerAxisEvent e)
    {
        var axis = (GameControllerAxis)e.Axis;
        short value = e.Value;

        if (axis == GameControllerAxis.Leftx)
        {
            int newState = value < -DeadZone ? -1 : value > DeadZone ? 1 : 0;

            if (newState == _lastAxisXState)
                return;

            if (newState == -1) OnLeft?.Invoke();
            if (newState == 1) OnRight?.Invoke();

            _lastAxisXState = newState;
            return;
        }

        if (axis == GameControllerAxis.Lefty)
        {
            int newState = value < -DeadZone ? -1 : value > DeadZone ? 1 : 0;

            if (newState == _lastAxisYState)
                return;

            if (newState == -1) OnUp?.Invoke();
            if (newState == 1) OnDown?.Invoke();

            _lastAxisYState = newState;
        }
    }

    public void Dispose()
    {
        StopMonitoring();
    }
}