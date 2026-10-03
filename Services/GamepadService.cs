using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Models;
using Silk.NET.SDL;

namespace Retromind.Services;

/// <summary>
/// Handles gamepad input via SDL2 (Silk.NET).
/// Supports hot-plugging and provides a small set of high-level navigation events.
/// </summary>
public sealed class GamepadService : IDisposable
{
    public enum GamepadDirection
    {
        Up,
        Down,
        Left,
        Right
    }

    // Events (raised on the SDL polling thread; subscribers should marshal to UI thread if needed).
    public event Action? OnSelect;
    public event Action? OnBack;
    public event Action? OnDetails;
    public event Action? OnHome;
    public event Action? OnExitBigMode;
    public event Action? OnSystemMenu;
    public event Action? OnPreviousPage;
    public event Action? OnNextPage;
    public event Action? OnSessionExitRequested;
    public event Action<ControllerButton>? OnButtonPressed;
    public event Action<GamepadDirection, bool>? OnDirectionStateChanged;

    private readonly AppSettings _settings;
    private readonly Sdl _sdl;
    private readonly bool _isSdlAvailable;
    private bool _sdlUnavailableLogged;

    private readonly Dictionary<int, IntPtr> _activeControllers = new();

    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    private bool _isInitialized;

    // SDL axis range is roughly -32768..32767; this filters small drift.
    private const int DeadZone = 15000;

    private static readonly TimeSpan SessionExitHoldDuration = TimeSpan.FromSeconds(2);

    private sealed class SessionExitHoldState
    {
        public bool FirstPressed { get; set; }
        public bool SecondPressed { get; set; }
        public long? HeldSinceTimestamp { get; set; }
        public bool WasRaised { get; set; }
    }

    private readonly Dictionary<int, SessionExitHoldState> _sessionExitHoldStates = new();

    private sealed class TriggerState
    {
        public bool LeftPressed { get; set; }
        public bool RightPressed { get; set; }
    }

    private readonly Dictionary<int, TriggerState> _triggerStates = new();

    // Simple axis edge detection to prevent event spam.
    // 0 = center, -1 = negative, 1 = positive
    private int _lastAxisXState;
    private int _lastAxisYState;
    private readonly object _uiInputGate = new();
    private bool _uiInputEnabled = true;

    public GamepadService(AppSettings? settings = null)
    {
        _settings = settings ?? new AppSettings();
        _settings.ControllerBindings ??= new ControllerBindingSettings();

        try
        {
            _sdl = Sdl.GetApi();
            _isSdlAvailable = true;
        }
        catch (Exception ex)
        {
            // SDL is optional. If it is not available, keep the app running without gamepad input.
            _sdl = null!;
            _isSdlAvailable = false;
            Debug.WriteLine($"[Gamepad] SDL unavailable, gamepad disabled: {ex.Message}");
        }
    }

    public void StartMonitoring()
    {
        StopMonitoring();

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        if (!_isSdlAvailable)
        {
            if (!_sdlUnavailableLogged)
            {
                _sdlUnavailableLogged = true;
                Debug.WriteLine("[Gamepad] Monitoring skipped: SDL2 runtime not available.");
            }
            return;
        }

        if (!EnsureInitialized())
        {
            // Initialization failed (e.g. SDL not available).
            return;
        }

        // Ensure controller events are enabled.
        _sdl.GameControllerEventState(Sdl.Enable);

        OpenExistingControllers();

        _loopTask = Task.Run(() => GameLoopAsync(token), token);
    }

    public void StopMonitoring()
    {
        var cts = _cts;
        var loopTask = _loopTask;
        _cts = null;
        _loopTask = null;

        cts?.Cancel();

        // The polling loop owns hot-plug updates. Let it finish before closing
        // controller handles so the dictionary cannot change during cleanup.
        if (loopTask != null)
        {
            try
            {
                loopTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation happens before the task starts.
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Gamepad] Monitoring loop stopped with an error: {ex.Message}");
            }
        }

        // Best effort: close all controllers.
        if (_isSdlAvailable)
        {
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
        }

        _activeControllers.Clear();
        _sessionExitHoldStates.Clear();
        _triggerStates.Clear();

        cts?.Dispose();

        // Important:
        // We intentionally do NOT call SDL_Quit() here.
        // Global Quit can affect other SDL consumers and may cause odd behavior across re-inits.
        // If you ever want full shutdown, expose a separate method that explicitly quits SDL.
    }

    /// <summary>
    /// Enables or suppresses controller events intended for the Retromind UI.
    /// SDL continues monitoring devices so controller input can be restored
    /// immediately when the application window becomes active again.
    /// </summary>
    public void SetUiInputEnabled(bool enabled)
    {
        lock (_uiInputGate)
        {
            if (_uiInputEnabled == enabled)
                return;

            _uiInputEnabled = enabled;
            _sessionExitHoldStates.Clear();
            _triggerStates.Clear();

            if (enabled)
                return;

            _lastAxisXState = 0;
            _lastAxisYState = 0;
        }

        // End any active repeat navigation before handing the controller to
        // another application. Releasing all directions is harmless for idle ones.
        foreach (var direction in Enum.GetValues<GamepadDirection>())
            OnDirectionStateChanged?.Invoke(direction, false);
    }

    private bool EnsureInitialized()
    {
        if (!_isSdlAvailable)
            return false;

        if (_isInitialized)
            return true;

        // We only need the GameController subsystem.
        // INIT_GAMECONTROLLER implies INIT_JOYSTICK.
        try
        {
            if (_sdl.Init(Sdl.InitGamecontroller) < 0)
            {
                Debug.WriteLine($"[SDL] Init failed: {_sdl.GetErrorS()}");
                _isInitialized = false;
                return false;
            }

            _isInitialized = true;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SDL] Init exception: {ex.Message}");
            _isInitialized = false;
            return false;
        }
    }

    private async Task GameLoopAsync(CancellationToken token)
    {
        Event sdlEvent;

        while (!token.IsCancellationRequested)
        {
            while (!token.IsCancellationRequested && PollNextEvent(out sdlEvent))
            {
                ProcessEvent(sdlEvent);
            }

            if (TryConsumeSessionExitHold())
                OnSessionExitRequested?.Invoke();

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

    private void OpenExistingControllers()
    {
        int joystickCount = _sdl.NumJoysticks();
        if (joystickCount <= 0)
            return;

        for (int index = 0; index < joystickCount; index++)
        {
            TryOpenControllerByIndex(index);
        }
    }

    private unsafe bool TryOpenControllerByIndex(int index)
    {
        if (_sdl.IsGameController(index) != SdlBool.True)
            return false;

        GameController* controller = _sdl.GameControllerOpen(index);
        if (controller == null)
            return false;

        Joystick* joystick = _sdl.GameControllerGetJoystick(controller);
        int instanceId = _sdl.JoystickInstanceID(joystick);

        if (_activeControllers.ContainsKey(instanceId))
        {
            _sdl.GameControllerClose(controller);
            return false;
        }

        _activeControllers[instanceId] = (IntPtr)controller;

        var name = _sdl.GameControllerNameS(controller);
        Debug.WriteLine($"[SDL] Controller connected: {name} (ID: {instanceId})");
        return true;
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
            case EventType.Controllerbuttonup:
            case EventType.Controlleraxismotion:
                HandleUiInputEvent(sdlEvent);
                break;
        }
    }

    private void HandleUiInputEvent(Event sdlEvent)
    {
        lock (_uiInputGate)
        {
            var eventType = (EventType)sdlEvent.Type;
            if (eventType is EventType.Controllerbuttondown or EventType.Controllerbuttonup &&
                TryMapButton((GameControllerButton)sdlEvent.Cbutton.Button, out var button))
            {
                var isPressed = eventType == EventType.Controllerbuttondown;
                UpdateSessionExitHoldState(sdlEvent.Cbutton.Which, button, isPressed);
                if (isPressed)
                    OnButtonPressed?.Invoke(button);
            }
            else if (eventType == EventType.Controlleraxismotion &&
                     TryUpdateTriggerState(sdlEvent.Caxis, out var trigger, out var isPressed))
            {
                UpdateSessionExitHoldState(sdlEvent.Caxis.Which, trigger, isPressed);
                if (isPressed)
                {
                    OnButtonPressed?.Invoke(trigger);
                    if (_uiInputEnabled)
                        RaiseMappedAction(trigger);
                }
            }

            if (!_uiInputEnabled)
                return;

            switch ((EventType)sdlEvent.Type)
            {
                case EventType.Controllerbuttondown:
                    HandleButtonDown(sdlEvent.Cbutton);
                    break;
                case EventType.Controllerbuttonup:
                    HandleButtonUp(sdlEvent.Cbutton);
                    break;
                case EventType.Controlleraxismotion:
                    HandleAxisMotion(sdlEvent.Caxis);
                    break;
            }
        }
    }

    private unsafe void HandleDeviceAdded(ControllerDeviceEvent e)
    {
        // "Which" is the device index in the system at the time of the event.
        int index = e.Which;
        TryOpenControllerByIndex(index);
    }

    private unsafe void HandleDeviceRemoved(ControllerDeviceEvent e)
    {
        // "Which" is the instance ID.
        int instanceId = e.Which;

        if (!_activeControllers.TryGetValue(instanceId, out var ptr))
            return;

        _sdl.GameControllerClose((GameController*)ptr);
        _activeControllers.Remove(instanceId);

        lock (_uiInputGate)
        {
            _sessionExitHoldStates.Remove(instanceId);
            _triggerStates.Remove(instanceId);
        }

        Debug.WriteLine($"[SDL] Controller disconnected (ID: {instanceId})");
    }

    private void HandleButtonDown(ControllerButtonEvent e)
    {
        // SDL maps buttons to a standard Xbox-style layout.
        var button = (GameControllerButton)e.Button;

        switch (button)
        {
            case GameControllerButton.DpadUp:
                RaiseDirectionState(GamepadDirection.Up, true);
                return;

            case GameControllerButton.DpadDown:
                RaiseDirectionState(GamepadDirection.Down, true);
                return;

            case GameControllerButton.DpadLeft:
                RaiseDirectionState(GamepadDirection.Left, true);
                return;

            case GameControllerButton.DpadRight:
                RaiseDirectionState(GamepadDirection.Right, true);
                return;
        }

        if (TryMapButton(button, out var mappedButton))
            RaiseMappedAction(mappedButton);
    }

    private void HandleButtonUp(ControllerButtonEvent e)
    {
        var button = (GameControllerButton)e.Button;

        switch (button)
        {
            case GameControllerButton.DpadUp:
                RaiseDirectionState(GamepadDirection.Up, false);
                break;
            case GameControllerButton.DpadDown:
                RaiseDirectionState(GamepadDirection.Down, false);
                break;
            case GameControllerButton.DpadLeft:
                RaiseDirectionState(GamepadDirection.Left, false);
                break;
            case GameControllerButton.DpadRight:
                RaiseDirectionState(GamepadDirection.Right, false);
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

            if (_lastAxisXState == -1) RaiseDirectionState(GamepadDirection.Left, false);
            if (_lastAxisXState == 1) RaiseDirectionState(GamepadDirection.Right, false);

            if (newState == -1) RaiseDirectionState(GamepadDirection.Left, true);
            if (newState == 1) RaiseDirectionState(GamepadDirection.Right, true);

            _lastAxisXState = newState;
            return;
        }

        if (axis == GameControllerAxis.Lefty)
        {
            int newState = value < -DeadZone ? -1 : value > DeadZone ? 1 : 0;

            if (newState == _lastAxisYState)
                return;

            if (_lastAxisYState == -1) RaiseDirectionState(GamepadDirection.Up, false);
            if (_lastAxisYState == 1) RaiseDirectionState(GamepadDirection.Down, false);

            if (newState == -1) RaiseDirectionState(GamepadDirection.Up, true);
            if (newState == 1) RaiseDirectionState(GamepadDirection.Down, true);

            _lastAxisYState = newState;
        }
    }

    private void RaiseDirectionState(GamepadDirection direction, bool isPressed) =>
        OnDirectionStateChanged?.Invoke(direction, isPressed);

    private void RaiseMappedAction(ControllerButton button)
    {
        var bindings = _settings.ControllerBindings;
        if (button == bindings.Select)
            OnSelect?.Invoke();
        else if (button == bindings.Back)
            OnBack?.Invoke();
        else if (button == bindings.Details)
            OnDetails?.Invoke();
        else if (button == bindings.Home)
            OnHome?.Invoke();
        else if (button == bindings.ExitBigMode)
            OnExitBigMode?.Invoke();
        else if (button == bindings.SystemMenu)
            OnSystemMenu?.Invoke();
        else if (button == bindings.PreviousPage)
            OnPreviousPage?.Invoke();
        else if (button == bindings.NextPage)
            OnNextPage?.Invoke();
    }

    private void UpdateSessionExitHoldState(int instanceId, ControllerButton button, bool isPressed)
    {
        var bindings = _settings.ControllerBindings;
        if (bindings.SessionStopFirst == bindings.SessionStopSecond)
            return;

        if (button != bindings.SessionStopFirst && button != bindings.SessionStopSecond)
            return;

        if (!_sessionExitHoldStates.TryGetValue(instanceId, out var state))
        {
            state = new SessionExitHoldState();
            _sessionExitHoldStates[instanceId] = state;
        }

        if (button == bindings.SessionStopFirst)
            state.FirstPressed = isPressed;
        if (button == bindings.SessionStopSecond)
            state.SecondPressed = isPressed;

        if (state.FirstPressed && state.SecondPressed)
        {
            state.HeldSinceTimestamp ??= Stopwatch.GetTimestamp();
            return;
        }

        state.HeldSinceTimestamp = null;
        state.WasRaised = false;
    }

    private bool TryUpdateTriggerState(
        ControllerAxisEvent axisEvent,
        out ControllerButton trigger,
        out bool isPressed)
    {
        var axis = (GameControllerAxis)axisEvent.Axis;
        if (axis is not (GameControllerAxis.Triggerleft or GameControllerAxis.Triggerright))
        {
            trigger = default;
            isPressed = false;
            return false;
        }

        if (!_triggerStates.TryGetValue(axisEvent.Which, out var state))
        {
            state = new TriggerState();
            _triggerStates[axisEvent.Which] = state;
        }

        trigger = axis == GameControllerAxis.Triggerleft
            ? ControllerButton.LeftTrigger
            : ControllerButton.RightTrigger;
        isPressed = axisEvent.Value > DeadZone;
        var wasPressed = axis == GameControllerAxis.Triggerleft
            ? state.LeftPressed
            : state.RightPressed;
        if (isPressed == wasPressed)
            return false;

        if (axis == GameControllerAxis.Triggerleft)
            state.LeftPressed = isPressed;
        else
            state.RightPressed = isPressed;

        return true;
    }

    internal static bool TryMapButton(GameControllerButton button, out ControllerButton mappedButton)
    {
        switch (button)
        {
            case GameControllerButton.A:
                mappedButton = ControllerButton.South;
                return true;
            case GameControllerButton.B:
                mappedButton = ControllerButton.East;
                return true;
            case GameControllerButton.X:
                mappedButton = ControllerButton.West;
                return true;
            case GameControllerButton.Y:
                mappedButton = ControllerButton.North;
                return true;
            case GameControllerButton.Back:
                mappedButton = ControllerButton.Back;
                return true;
            case GameControllerButton.Guide:
                mappedButton = ControllerButton.Guide;
                return true;
            case GameControllerButton.Start:
                mappedButton = ControllerButton.Start;
                return true;
            case GameControllerButton.Leftstick:
                mappedButton = ControllerButton.LeftStick;
                return true;
            case GameControllerButton.Rightstick:
                mappedButton = ControllerButton.RightStick;
                return true;
            case GameControllerButton.Leftshoulder:
                mappedButton = ControllerButton.LeftShoulder;
                return true;
            case GameControllerButton.Rightshoulder:
                mappedButton = ControllerButton.RightShoulder;
                return true;
            case GameControllerButton.Misc1:
                mappedButton = ControllerButton.Misc;
                return true;
            case GameControllerButton.Paddle1:
                mappedButton = ControllerButton.Paddle1;
                return true;
            case GameControllerButton.Paddle2:
                mappedButton = ControllerButton.Paddle2;
                return true;
            case GameControllerButton.Paddle3:
                mappedButton = ControllerButton.Paddle3;
                return true;
            case GameControllerButton.Paddle4:
                mappedButton = ControllerButton.Paddle4;
                return true;
            case GameControllerButton.Touchpad:
                mappedButton = ControllerButton.Touchpad;
                return true;
            default:
                mappedButton = default;
                return false;
        }
    }

    private bool TryConsumeSessionExitHold()
    {
        lock (_uiInputGate)
        {
            if (_uiInputEnabled)
                return false;

            foreach (var state in _sessionExitHoldStates.Values)
            {
                if (state.WasRaised || state.HeldSinceTimestamp is not { } heldSince)
                    continue;

                if (Stopwatch.GetElapsedTime(heldSince) < SessionExitHoldDuration)
                    continue;

                state.WasRaised = true;
                return true;
            }

            return false;
        }
    }

    public void Dispose()
    {
        StopMonitoring();
    }
}
