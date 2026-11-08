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

    // Events normally run on the SDL polling thread; lifecycle releases can run on the caller.
    // Subscribers must marshal to the UI thread. No subscriber runs under the input-state lock.
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
    private readonly TimeProvider _timeProvider;
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

    private sealed class DirectionState
    {
        public int AxisX;
        public int AxisY;
        public int Dpad;

        public int Pressed => Dpad |
            (AxisX < 0 ? DirectionBit(GamepadDirection.Left) : AxisX > 0 ? DirectionBit(GamepadDirection.Right) : 0) |
            (AxisY < 0 ? DirectionBit(GamepadDirection.Up) : AxisY > 0 ? DirectionBit(GamepadDirection.Down) : 0);
    }

    private readonly Dictionary<int, DirectionState> _directionStates = new();
    private int _pressedDirections;
    private int _publishedDirections;
    private readonly object _uiInputGate = new();
    private bool _uiInputEnabled = true;
    private bool _monitoringStopped;
    private long _inputGeneration;
    private long _monitoringGeneration;
    private readonly Queue<PendingInputEvent> _pendingInputEvents = new();
    private bool _dispatchingEvents;

    private sealed record PendingInputEvent(
        Action? Callback, long Generation, bool UiOnly = false, bool BackgroundOnly = false,
        GamepadDirection? Direction = null, bool IsPressed = false);

    public GamepadService(AppSettings? settings = null)
        : this(settings, loadSdl: true)
    {
    }

    // Tests drive the same SDL event processor without opening native controllers or sleeping for holds.
    internal GamepadService(AppSettings? settings, bool loadSdl, TimeProvider? timeProvider = null)
    {
        _settings = settings ?? new AppSettings();
        _settings.ControllerBindings ??= new ControllerBindingSettings();
        _timeProvider = timeProvider ?? TimeProvider.System;

        if (!loadSdl)
        {
            _sdl = null!;
            return;
        }

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

    public void StartMonitoring() => StartMonitoring(expectedGeneration: null);

    private void StartMonitoring(long? expectedGeneration)
    {
        var stop = RequestStop(expectedGeneration);
        if (stop == null)
            return;
        FinishStop(stop.Value.Loop);

        lock (_uiInputGate)
        {
            // A callback/concurrent StopMonitoring may have canceled this start while releases were delivered.
            if (_monitoringGeneration != stop.Value.Generation)
                return;
        }

        // A subscriber may request a restart from inside the polling callback. Never join that loop here.
        if (stop.Value.Loop is { IsCompleted: false } previousLoop)
        {
            _ = RestartAfterLoopAsync(previousLoop, stop.Value.Generation);
            return;
        }

        lock (_uiInputGate)
        {
            if (_monitoringGeneration != stop.Value.Generation)
                return;
            _monitoringStopped = false;
        }

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

        lock (_uiInputGate)
        {
            if (_monitoringGeneration == stop.Value.Generation)
            {
                var cts = new CancellationTokenSource();
                _cts = cts;
                // Always enter the loop's finally, even if StopMonitoring immediately cancels the token.
                _loopTask = Task.Run(() => GameLoopAsync(cts));
                return;
            }
        }
        CloseControllers(); // Startup was canceled before the polling loop took ownership.
    }

    public void StopMonitoring()
    {
        var stop = RequestStop();
        FinishStop(stop!.Value.Loop);
    }

    private (Task? Loop, long Generation)? RequestStop(long? expectedGeneration = null)
    {
        lock (_uiInputGate)
        {
            if (expectedGeneration.HasValue && _monitoringGeneration != expectedGeneration.Value)
                return null;
            _monitoringGeneration++;
            _monitoringStopped = true;
            ResetInputState();
            _cts?.Cancel();
            return (_loopTask, _monitoringGeneration);
        }
    }

    private void FinishStop(Task? loopTask)
    {
        DrainInputEvents();

        bool dispatchInProgress;
        lock (_uiInputGate)
            dispatchInProgress = _dispatchingEvents;

        // The polling loop owns hot-plug updates. Let it finish before closing
        // controller handles so the dictionary cannot change during cleanup.
        if (loopTask != null && !dispatchInProgress)
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

        // Native handles and the token source are owned by the loop's finally. Callback-triggered
        // stops (including cross-thread calls from a subscriber) never wait on an in-flight callback.
        // Ordinary callers with no callback in flight still wait for native cleanup.
    }

    private async Task RestartAfterLoopAsync(Task previousLoop, long generation)
    {
        try { await previousLoop.ConfigureAwait(false); }
        catch (Exception ex) { Debug.WriteLine($"[Gamepad] Previous loop ended with an error: {ex.Message}"); }
        StartMonitoring(generation);
    }

    private void CloseControllers()
    {
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
            ResetInputState();
        }

        DrainInputEvents();
    }

    // Called under _uiInputGate. Clear both on disable and enable; background events never restore UI holds.
    private void ResetInputState()
    {
        _inputGeneration++;
        _sessionExitHoldStates.Clear();
        _triggerStates.Clear();
        _directionStates.Clear();
        UpdateAggregatedDirections();
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

    private async Task GameLoopAsync(CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                while (!token.IsCancellationRequested && PollNextEvent(out var sdlEvent))
                    ProcessEvent(sdlEvent);

                CheckSessionExitHold();

                try { await Task.Delay(10, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            CloseControllers();
            lock (_uiInputGate)
            {
                _monitoringStopped = true;
                ResetInputState();
                _cts = null;
                cts.Dispose();
            }
            DrainInputEvents();
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

    internal void ProcessEvent(Event sdlEvent)
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
            if (_monitoringStopped)
                return;
            var eventType = (EventType)sdlEvent.Type;
            if (eventType is EventType.Controllerbuttondown or EventType.Controllerbuttonup &&
                TryMapButton((GameControllerButton)sdlEvent.Cbutton.Button, out var button))
            {
                var isPressed = eventType == EventType.Controllerbuttondown;
                UpdateSessionExitHoldState(sdlEvent.Cbutton.Which, button, isPressed);
                if (isPressed)
                    QueueInputEvent(() => OnButtonPressed?.Invoke(button));
            }
            else if (eventType == EventType.Controlleraxismotion &&
                     TryUpdateTriggerState(sdlEvent.Caxis, out var trigger, out var isPressed))
            {
                UpdateSessionExitHoldState(sdlEvent.Caxis.Which, trigger, isPressed);
                if (isPressed)
                {
                    QueueInputEvent(() => OnButtonPressed?.Invoke(trigger));
                    if (_uiInputEnabled)
                        RaiseMappedAction(trigger);
                }
            }

            if (_uiInputEnabled)
            {
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
        DrainInputEvents();
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

        lock (_uiInputGate)
        {
            _sessionExitHoldStates.Remove(instanceId);
            _triggerStates.Remove(instanceId);
            _directionStates.Remove(instanceId);
            UpdateAggregatedDirections();
        }
        DrainInputEvents();

        if (_activeControllers.Remove(instanceId, out var ptr))
            _sdl.GameControllerClose((GameController*)ptr);

        Debug.WriteLine($"[SDL] Controller disconnected (ID: {instanceId})");
    }

    private void HandleButtonDown(ControllerButtonEvent e)
    {
        // SDL maps buttons to a standard Xbox-style layout.
        var button = (GameControllerButton)e.Button;

        switch (button)
        {
            case GameControllerButton.DpadUp:
                UpdateDpadState(e.Which, GamepadDirection.Up, true);
                return;

            case GameControllerButton.DpadDown:
                UpdateDpadState(e.Which, GamepadDirection.Down, true);
                return;

            case GameControllerButton.DpadLeft:
                UpdateDpadState(e.Which, GamepadDirection.Left, true);
                return;

            case GameControllerButton.DpadRight:
                UpdateDpadState(e.Which, GamepadDirection.Right, true);
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
                UpdateDpadState(e.Which, GamepadDirection.Up, false);
                break;
            case GameControllerButton.DpadDown:
                UpdateDpadState(e.Which, GamepadDirection.Down, false);
                break;
            case GameControllerButton.DpadLeft:
                UpdateDpadState(e.Which, GamepadDirection.Left, false);
                break;
            case GameControllerButton.DpadRight:
                UpdateDpadState(e.Which, GamepadDirection.Right, false);
                break;
        }
    }

    private void HandleAxisMotion(ControllerAxisEvent e)
    {
        var axis = (GameControllerAxis)e.Axis;
        if (axis is not (GameControllerAxis.Leftx or GameControllerAxis.Lefty))
            return;

        var state = GetDirectionState(e.Which);
        var newState = e.Value < -DeadZone ? -1 : e.Value > DeadZone ? 1 : 0;
        if (axis == GameControllerAxis.Leftx)
            state.AxisX = newState;
        else
            state.AxisY = newState;
        UpdateAggregatedDirections();
    }

    private DirectionState GetDirectionState(int instanceId)
    {
        if (!_directionStates.TryGetValue(instanceId, out var state))
            _directionStates[instanceId] = state = new DirectionState();
        return state;
    }

    private static int DirectionBit(GamepadDirection direction) => 1 << (int)direction;

    private void UpdateDpadState(int instanceId, GamepadDirection direction, bool pressed)
    {
        var state = GetDirectionState(instanceId);
        var bit = DirectionBit(direction);
        state.Dpad = pressed ? state.Dpad | bit : state.Dpad & ~bit;
        UpdateAggregatedDirections();
    }

    // All source changes and queue operations are protected by _uiInputGate.
    private void UpdateAggregatedDirections()
    {
        var next = 0;
        foreach (var state in _directionStates.Values)
            next |= state.Pressed;
        if (next == _pressedDirections)
            return;

        // Release old directions before pressing new ones (in particular, a direct left/right switch).
        foreach (var direction in Enum.GetValues<GamepadDirection>())
        {
            var bit = DirectionBit(direction);
            if ((_pressedDirections & bit) != 0 && (next & bit) == 0)
                _pendingInputEvents.Enqueue(new(null, _inputGeneration, Direction: direction, IsPressed: false));
        }
        foreach (var direction in Enum.GetValues<GamepadDirection>())
        {
            var bit = DirectionBit(direction);
            if ((_pressedDirections & bit) == 0 && (next & bit) != 0)
                _pendingInputEvents.Enqueue(new(null, _inputGeneration, Direction: direction, IsPressed: true));
        }
        _pressedDirections = next;
    }

    private void QueueInputEvent(Action callback, bool uiOnly = false, bool backgroundOnly = false) =>
        _pendingInputEvents.Enqueue(new(callback, _inputGeneration, uiOnly, backgroundOnly));

    private void DrainInputEvents()
    {
        lock (_uiInputGate)
        {
            if (_dispatchingEvents)
                return;
            _dispatchingEvents = true;
        }

        while (true)
        {
            Action? callback;
            lock (_uiInputGate)
            {
                if (!_pendingInputEvents.TryDequeue(out var pending))
                {
                    _dispatchingEvents = false;
                    return;
                }

                if (pending.Direction is { } direction)
                {
                    if (pending.IsPressed &&
                        (pending.Generation != _inputGeneration || !_uiInputEnabled || _monitoringStopped))
                        continue;
                    var bit = DirectionBit(direction);
                    if (((_publishedDirections & bit) != 0) == pending.IsPressed)
                        continue;
                    _publishedDirections = pending.IsPressed ? _publishedDirections | bit : _publishedDirections & ~bit;
                    callback = () => OnDirectionStateChanged?.Invoke(direction, pending.IsPressed);
                }
                else
                {
                    if (pending.Generation != _inputGeneration || _monitoringStopped ||
                        (pending.UiOnly && !_uiInputEnabled) || (pending.BackgroundOnly && _uiInputEnabled))
                        continue;
                    callback = pending.Callback;
                }
            }

            // A callback can change focus, stop monitoring, or wait for another thread that does so.
            // Reentrant/concurrent operations append to the same queue; only this drainer invokes subscribers.
            try { callback?.Invoke(); }
            catch (Exception ex) { Debug.WriteLine($"[Gamepad] Input subscriber failed: {ex.Message}"); }
        }
    }

    private void RaiseMappedAction(ControllerButton button)
    {
        var bindings = _settings.ControllerBindings;
        if (button == bindings.Select)
            QueueInputEvent(() => OnSelect?.Invoke(), uiOnly: true);
        else if (button == bindings.Back)
            QueueInputEvent(() => OnBack?.Invoke(), uiOnly: true);
        else if (button == bindings.Details)
            QueueInputEvent(() => OnDetails?.Invoke(), uiOnly: true);
        else if (button == bindings.Home)
            QueueInputEvent(() => OnHome?.Invoke(), uiOnly: true);
        else if (button == bindings.ExitBigMode)
            QueueInputEvent(() => OnExitBigMode?.Invoke(), uiOnly: true);
        else if (button == bindings.SystemMenu)
            QueueInputEvent(() => OnSystemMenu?.Invoke(), uiOnly: true);
        else if (button == bindings.PreviousPage)
            QueueInputEvent(() => OnPreviousPage?.Invoke(), uiOnly: true);
        else if (button == bindings.NextPage)
            QueueInputEvent(() => OnNextPage?.Invoke(), uiOnly: true);
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
            state.HeldSinceTimestamp ??= _timeProvider.GetTimestamp();
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
            if (_uiInputEnabled || _monitoringStopped)
                return false;

            foreach (var state in _sessionExitHoldStates.Values)
            {
                if (state.WasRaised || state.HeldSinceTimestamp is not { } heldSince)
                    continue;

                if (_timeProvider.GetElapsedTime(heldSince) < SessionExitHoldDuration)
                    continue;

                state.WasRaised = true;
                return true;
            }

            return false;
        }
    }

    internal void CheckSessionExitHold()
    {
        lock (_uiInputGate)
        {
            if (TryConsumeSessionExitHold())
                QueueInputEvent(() => OnSessionExitRequested?.Invoke(), backgroundOnly: true);
        }
        DrainInputEvents();
    }

    public void Dispose()
    {
        StopMonitoring();
    }
}
