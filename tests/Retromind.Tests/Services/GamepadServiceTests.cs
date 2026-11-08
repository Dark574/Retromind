using Retromind.Models;
using System.Reflection;
using Retromind.Services;
using Silk.NET.SDL;
using Direction = Retromind.Services.GamepadService.GamepadDirection;

namespace Retromind.Tests.Services;

public sealed class GamepadServiceTests
{
    [Theory]
    [InlineData(GameControllerButton.A, ControllerButton.South)]
    [InlineData(GameControllerButton.B, ControllerButton.East)]
    [InlineData(GameControllerButton.X, ControllerButton.West)]
    [InlineData(GameControllerButton.Y, ControllerButton.North)]
    [InlineData(GameControllerButton.Back, ControllerButton.Back)]
    [InlineData(GameControllerButton.Guide, ControllerButton.Guide)]
    [InlineData(GameControllerButton.Start, ControllerButton.Start)]
    [InlineData(GameControllerButton.Leftstick, ControllerButton.LeftStick)]
    [InlineData(GameControllerButton.Rightstick, ControllerButton.RightStick)]
    [InlineData(GameControllerButton.Leftshoulder, ControllerButton.LeftShoulder)]
    [InlineData(GameControllerButton.Rightshoulder, ControllerButton.RightShoulder)]
    [InlineData(GameControllerButton.Misc1, ControllerButton.Misc)]
    [InlineData(GameControllerButton.Paddle1, ControllerButton.Paddle1)]
    [InlineData(GameControllerButton.Paddle2, ControllerButton.Paddle2)]
    [InlineData(GameControllerButton.Paddle3, ControllerButton.Paddle3)]
    [InlineData(GameControllerButton.Paddle4, ControllerButton.Paddle4)]
    [InlineData(GameControllerButton.Touchpad, ControllerButton.Touchpad)]
    public void TryMapButton_MapsAssignableSdlButtons(
        GameControllerButton source,
        ControllerButton expected)
    {
        Assert.True(GamepadService.TryMapButton(source, out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TryMapButton_LeavesDirectionalNavigationFixed()
    {
        Assert.False(GamepadService.TryMapButton(GameControllerButton.DpadUp, out _));
    }

    [Theory]
    [InlineData(GameControllerAxis.Leftx, Direction.Right)]
    [InlineData(GameControllerAxis.Lefty, Direction.Down)]
    public void AxisStates_AggregateTwoControllersWithoutPrematureRelease(GameControllerAxis axis, Direction direction)
    {
        using var service = CreateService();
        var events = RecordDirections(service);

        Axis(service, 11, axis, 20_000);
        Axis(service, 22, axis, 20_000);
        Axis(service, 11, axis, 0);
        Assert.Equal([(direction, true)], events);
        Axis(service, 22, axis, 0);
        Assert.Equal([(direction, true), (direction, false)], events);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DpadAndStick_HoldDirectionUntilBothSourcesRelease(bool releaseStickFirst)
    {
        using var service = CreateService();
        var events = RecordDirections(service);
        Axis(service, 11, GameControllerAxis.Leftx, -20_000);
        Button(service, 11, GameControllerButton.DpadLeft, true);

        if (releaseStickFirst) Axis(service, 11, GameControllerAxis.Leftx, 0);
        else Button(service, 11, GameControllerButton.DpadLeft, false);
        Assert.Equal([(Direction.Left, true)], events);

        if (releaseStickFirst) Button(service, 11, GameControllerButton.DpadLeft, false);
        else Axis(service, 11, GameControllerAxis.Leftx, 0);
        Assert.Equal([(Direction.Left, true), (Direction.Left, false)], events);
    }

    [Fact]
    public void DpadStates_AggregateAcrossControllersAndIgnoreDuplicateEdges()
    {
        using var service = CreateService();
        var events = RecordDirections(service);
        Button(service, 1, GameControllerButton.DpadUp, true);
        Button(service, 1, GameControllerButton.DpadUp, true);
        Button(service, 2, GameControllerButton.DpadUp, true);
        Button(service, 1, GameControllerButton.DpadUp, false);
        Button(service, 1, GameControllerButton.DpadUp, false);
        Assert.Equal([(Direction.Up, true)], events);
        Button(service, 2, GameControllerButton.DpadUp, false);
        Assert.Equal([(Direction.Up, true), (Direction.Up, false)], events);
    }

    [Fact]
    public void AxisSwitch_ReleasesLeftBeforePressingRightAndDoesNotRepeatUnchangedState()
    {
        using var service = CreateService();
        var events = RecordDirections(service);
        Axis(service, 1, GameControllerAxis.Leftx, -20_000);
        Axis(service, 1, GameControllerAxis.Leftx, -30_000);
        Axis(service, 1, GameControllerAxis.Leftx, 20_000);
        Axis(service, 1, GameControllerAxis.Leftx, 30_000);
        Axis(service, 1, GameControllerAxis.Leftx, 0);
        Assert.Equal([(Direction.Left, true), (Direction.Left, false),
            (Direction.Right, true), (Direction.Right, false)], events);
    }

    [Fact]
    public void OppositeSources_KeepLeftHeldDuringAnotherControllersRightToCenterTransition()
    {
        using var service = CreateService();
        var events = RecordDirections(service);
        Axis(service, 1, GameControllerAxis.Leftx, -20_000);
        Axis(service, 2, GameControllerAxis.Leftx, 20_000);
        Axis(service, 2, GameControllerAxis.Leftx, 0);
        Axis(service, 1, GameControllerAxis.Leftx, 0);
        Assert.Equal([(Direction.Left, true), (Direction.Right, true),
            (Direction.Right, false), (Direction.Left, false)], events);
    }

    [Theory]
    [InlineData(GameControllerAxis.Leftx)]
    [InlineData(GameControllerAxis.Lefty)]
    public void Deadzone_KeepsExistingInclusiveThresholdAndIgnoresDrift(GameControllerAxis axis)
    {
        using var service = CreateService();
        var events = RecordDirections(service);
        foreach (short value in new short[] { -15_000, -1, 0, 1, 15_000 })
            Axis(service, 1, axis, value);
        Assert.Empty(events);

        Axis(service, 1, axis, 15_001);
        Axis(service, 1, axis, 15_000);
        Axis(service, 1, axis, -15_000);
        var direction = axis == GameControllerAxis.Leftx ? Direction.Right : Direction.Down;
        Assert.Equal([(direction, true), (direction, false)], events);
    }

    [Fact]
    public void ControllerRemoval_ReleasesOnlyDirectionsNoOtherControllerHolds()
    {
        using var service = CreateService();
        var events = RecordDirections(service);
        Axis(service, 1, GameControllerAxis.Leftx, -20_000);
        Button(service, 1, GameControllerButton.DpadUp, true);
        Button(service, 2, GameControllerButton.DpadUp, true);
        Remove(service, 1);
        Remove(service, 1); // Repeated/unknown removal must not fabricate releases.
        Assert.Equal([(Direction.Left, true), (Direction.Up, true), (Direction.Left, false)], events);
        Remove(service, 2);
        Assert.Equal((Direction.Up, false), events[^1]);
        Assert.Equal(4, events.Count);

        // Reusing the identifier starts with fresh axis and D-pad states.
        Axis(service, 1, GameControllerAxis.Leftx, -20_000);
        Assert.Equal((Direction.Left, true), events[^1]);
    }

    [Fact]
    public void StopMonitoring_ReleasesAllDirectionsOnceAndRestartHasFreshState()
    {
        using var service = CreateService();
        var events = RecordDirections(service);
        Axis(service, 1, GameControllerAxis.Leftx, -20_000);
        Button(service, 1, GameControllerButton.DpadLeft, true);
        Button(service, 2, GameControllerButton.DpadDown, true);

        service.StopMonitoring();
        service.StopMonitoring();
        Assert.Equal([(Direction.Left, true), (Direction.Down, true),
            (Direction.Down, false), (Direction.Left, false)], events);
        Axis(service, 1, GameControllerAxis.Leftx, -20_000);
        Assert.Equal(4, events.Count); // A stopped processor ignores late events.

        service.StartMonitoring(); // Native loading is disabled in this fixture.
        Assert.Equal(4, events.Count);
        Axis(service, 1, GameControllerAxis.Leftx, -20_000);
        Assert.Equal((Direction.Left, true), events[^1]);
    }

    [Fact]
    public void UiActivation_ReleasesActiveDirectionsAndNeverReplaysBackgroundInput()
    {
        using var service = CreateService();
        var events = RecordDirections(service);
        Axis(service, 1, GameControllerAxis.Leftx, -20_000);
        Button(service, 2, GameControllerButton.DpadUp, true);
        service.SetUiInputEnabled(false);
        service.SetUiInputEnabled(false);
        Assert.Equal([(Direction.Left, true), (Direction.Up, true),
            (Direction.Up, false), (Direction.Left, false)], events);

        Axis(service, 1, GameControllerAxis.Leftx, 20_000);
        Button(service, 2, GameControllerButton.DpadDown, true);
        service.SetUiInputEnabled(true);
        Assert.Equal(4, events.Count);
        Axis(service, 1, GameControllerAxis.Leftx, 0);
        Button(service, 2, GameControllerButton.DpadDown, false);
        Assert.Equal(4, events.Count);
        Axis(service, 1, GameControllerAxis.Leftx, 20_000);
        Assert.Equal((Direction.Right, true), events[^1]);
    }

    [Fact]
    public void DirectionCallback_CanDisableUiReentrantlyWithoutRestoringOldAxisState()
    {
        using var service = CreateService();
        var events = RecordDirections(service);
        service.OnDirectionStateChanged += (_, pressed) =>
        {
            if (pressed) service.SetUiInputEnabled(false);
        };
        Axis(service, 1, GameControllerAxis.Leftx, -20_000);
        Assert.Equal([(Direction.Left, true), (Direction.Left, false)], events);
        service.SetUiInputEnabled(true);
        Axis(service, 1, GameControllerAxis.Leftx, 0);
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public async Task BlockedCallback_DoesNotBlockFocusChangeOrReplayQueuedPresses()
    {
        using var service = CreateService();
        var events = RecordDirections(service);
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        service.OnDirectionStateChanged += (direction, pressed) =>
        {
            if (direction == Direction.Left && pressed)
            {
                entered.Set();
                Assert.True(finish.Wait(TimeSpan.FromSeconds(5)));
            }
        };
        var polling = Task.Run(() => Axis(service, 1, GameControllerAxis.Leftx, -20_000));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Axis(service, 1, GameControllerAxis.Lefty, -20_000); // Queued behind the blocked callback.
            await Task.Run(() => service.SetUiInputEnabled(false)).WaitAsync(TimeSpan.FromSeconds(5));
            service.SetUiInputEnabled(true);
        }
        finally
        {
            finish.Set();
            await polling.WaitAsync(TimeSpan.FromSeconds(5));
        }
        // The queued Up press was invalidated; its unmatched release must also be suppressed.
        Assert.Equal([(Direction.Left, true), (Direction.Left, false)], events);
    }

    [Fact]
    public void ButtonCapture_CanDisableUiFromAnotherThreadWithoutDeadlockOrStaleMappedAction()
    {
        using var service = CreateService();
        var selects = 0;
        service.OnSelect += () => selects++;
        Task? disable = null;
        service.OnButtonPressed += _ =>
        {
            disable = Task.Run(() => service.SetUiInputEnabled(false));
            Assert.True(disable.Wait(TimeSpan.FromSeconds(5)));
        };

        Button(service, 1, GameControllerButton.A, true);

        Assert.NotNull(disable);
        Assert.True(disable.IsCompletedSuccessfully);
        Assert.Equal(0, selects);
    }

    [Fact]
    public void ConfiguredActionButton_KeepsMappingAndCaptureBehavior()
    {
        var settings = new AppSettings { ControllerBindings = new() { Select = ControllerButton.RightStick } };
        using var service = CreateService(settings);
        var selects = 0;
        var captured = new List<ControllerButton>();
        service.OnSelect += () => selects++;
        service.OnButtonPressed += captured.Add;
        Button(service, 1, GameControllerButton.Rightstick, true);
        Button(service, 1, GameControllerButton.Rightstick, false);
        service.SetUiInputEnabled(false);
        Button(service, 1, GameControllerButton.Rightstick, true);
        Assert.Equal(1, selects);
        Assert.Equal([ControllerButton.RightStick, ControllerButton.RightStick], captured);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PollingCallback_CanStopMonitoringWithoutJoiningItselfOrBlockingAnotherThread(bool crossThread)
    {
        using var service = CreateService();
        var events = RecordDirections(service);
        using var start = new ManualResetEventSlim();
        Task? stopping = null;
        var returned = false;
        service.OnDirectionStateChanged += (_, pressed) =>
        {
            if (!pressed) return;
            if (crossThread)
            {
                stopping = Task.Run(service.StopMonitoring);
                returned = stopping.Wait(TimeSpan.FromSeconds(5));
            }
            else
            {
                service.StopMonitoring();
                returned = true;
            }
        };
        // This task stands in for the owned SDL loop. A join from its callback would deadlock.
        var polling = Task.Run(() =>
        {
            Assert.True(start.Wait(TimeSpan.FromSeconds(5)));
            Axis(service, 1, GameControllerAxis.Leftx, -20_000);
        });
        typeof(GamepadService).GetField("_loopTask", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, polling);
        start.Set();

        await polling.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(returned);
        if (stopping != null) Assert.True(stopping.IsCompletedSuccessfully);
        Assert.Equal([(Direction.Left, true), (Direction.Left, false)], events);
    }

    [Fact]
    public void FailingSubscriber_DoesNotStrandTheEventQueueOrPreventReleases()
    {
        using var service = CreateService();
        var events = RecordDirections(service);
        service.OnDirectionStateChanged += (_, _) => throw new InvalidOperationException("Subscriber failed");
        Axis(service, 1, GameControllerAxis.Leftx, -20_000);
        service.StopMonitoring();
        Assert.Equal([(Direction.Left, true), (Direction.Left, false)], events);
    }

    [Fact]
    public void StopDuringStartupRelease_CancelsRestartWithoutRestoringDirections()
    {
        using var service = CreateService();
        var events = RecordDirections(service);
        Axis(service, 1, GameControllerAxis.Leftx, -20_000);
        service.OnDirectionStateChanged += (_, pressed) =>
        {
            if (!pressed) service.StopMonitoring();
        };

        service.StartMonitoring();
        Axis(service, 1, GameControllerAxis.Leftx, -20_000);

        Assert.Equal([(Direction.Left, true), (Direction.Left, false)], events);
    }

    [Fact]
    public void Triggers_KeepPerControllerEdgeDetectionAndConfigurableActions()
    {
        var settings = new AppSettings { ControllerBindings = new() { NextPage = ControllerButton.LeftTrigger } };
        using var service = CreateService(settings);
        var pages = 0;
        var captured = new List<ControllerButton>();
        service.OnNextPage += () => pages++;
        service.OnButtonPressed += captured.Add;
        Axis(service, 1, GameControllerAxis.Triggerleft, 15_000);
        Axis(service, 1, GameControllerAxis.Triggerleft, 20_000);
        Axis(service, 1, GameControllerAxis.Triggerleft, 30_000);
        Axis(service, 2, GameControllerAxis.Triggerleft, 20_000);
        Axis(service, 1, GameControllerAxis.Triggerleft, 0);
        Axis(service, 1, GameControllerAxis.Triggerleft, 20_000);
        Assert.Equal(3, pages);
        Assert.Equal(Enumerable.Repeat(ControllerButton.LeftTrigger, 3), captured);
        service.SetUiInputEnabled(false);
        Axis(service, 1, GameControllerAxis.Triggerleft, 20_000);
        Axis(service, 1, GameControllerAxis.Triggerleft, 30_000);
        Assert.Equal(3, pages);
        Assert.Equal(4, captured.Count);
    }

    [Fact]
    public void SessionExit_RequiresBothButtonsOnSameControllerForTwoSecondsAndRearmsOnRelease()
    {
        var clock = new ManualTimeProvider();
        using var service = CreateService(clock: clock);
        var exits = 0;
        service.OnSessionExitRequested += () => exits++;
        Button(service, 1, GameControllerButton.Leftshoulder, true);
        Button(service, 1, GameControllerButton.Rightshoulder, true);
        clock.Advance(TimeSpan.FromSeconds(3));
        service.CheckSessionExitHold();
        Assert.Equal(0, exits); // Session stop is active only while UI input is disabled.

        service.SetUiInputEnabled(false);
        Button(service, 1, GameControllerButton.Leftshoulder, true);
        Button(service, 2, GameControllerButton.Rightshoulder, true);
        clock.Advance(TimeSpan.FromSeconds(3));
        service.CheckSessionExitHold();
        Assert.Equal(0, exits);

        Button(service, 1, GameControllerButton.Rightshoulder, true);
        clock.Advance(TimeSpan.FromMilliseconds(1999));
        service.CheckSessionExitHold();
        Assert.Equal(0, exits);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        service.CheckSessionExitHold();
        service.CheckSessionExitHold();
        Assert.Equal(1, exits);

        Button(service, 1, GameControllerButton.Rightshoulder, false);
        Button(service, 1, GameControllerButton.Rightshoulder, true);
        clock.Advance(TimeSpan.FromSeconds(2));
        service.CheckSessionExitHold();
        Assert.Equal(2, exits);
    }

    [Fact]
    public void SessionExit_TriggerCombinationSurvivesUiSuppressionAndIsClearedOnRemovalOrFocusChange()
    {
        var settings = new AppSettings { ControllerBindings = new()
        {
            SessionStopFirst = ControllerButton.LeftTrigger, SessionStopSecond = ControllerButton.RightTrigger
        } };
        var clock = new ManualTimeProvider();
        using var service = CreateService(settings, clock);
        var exits = 0;
        service.OnSessionExitRequested += () => exits++;
        service.SetUiInputEnabled(false);
        Axis(service, 1, GameControllerAxis.Triggerleft, 20_000);
        Axis(service, 1, GameControllerAxis.Triggerright, 20_000);
        clock.Advance(TimeSpan.FromSeconds(2));
        service.CheckSessionExitHold();
        Assert.Equal(1, exits);

        Remove(service, 1);
        clock.Advance(TimeSpan.FromSeconds(5));
        service.CheckSessionExitHold();
        Assert.Equal(1, exits);
        Axis(service, 1, GameControllerAxis.Triggerleft, 20_000);
        Axis(service, 1, GameControllerAxis.Triggerright, 20_000);
        service.SetUiInputEnabled(true);
        service.SetUiInputEnabled(false);
        clock.Advance(TimeSpan.FromSeconds(5));
        service.CheckSessionExitHold();
        Assert.Equal(1, exits);
        Axis(service, 1, GameControllerAxis.Triggerleft, 20_000);
        Axis(service, 1, GameControllerAxis.Triggerright, 20_000);
        clock.Advance(TimeSpan.FromSeconds(2));
        service.CheckSessionExitHold();
        Assert.Equal(2, exits);
    }

    private static GamepadService CreateService(AppSettings? settings = null, TimeProvider? clock = null) =>
        new(settings, loadSdl: false, timeProvider: clock);

    private static List<(Direction, bool)> RecordDirections(GamepadService service)
    {
        var events = new List<(Direction, bool)>();
        service.OnDirectionStateChanged += (direction, pressed) => events.Add((direction, pressed));
        return events;
    }

    private static void Axis(GamepadService service, int instanceId, GameControllerAxis axis, short value) =>
        service.ProcessEvent(new Event { Caxis = new ControllerAxisEvent
        {
            Type = (uint)EventType.Controlleraxismotion, Which = instanceId, Axis = (byte)axis, Value = value
        } });

    private static void Button(GamepadService service, int instanceId, GameControllerButton button, bool pressed) =>
        service.ProcessEvent(new Event { Cbutton = new ControllerButtonEvent
        {
            Type = (uint)(pressed ? EventType.Controllerbuttondown : EventType.Controllerbuttonup),
            Which = instanceId, Button = (byte)button
        } });

    private static void Remove(GamepadService service, int instanceId) =>
        service.ProcessEvent(new Event { Cdevice = new ControllerDeviceEvent
        {
            Type = (uint)EventType.Controllerdeviceremoved, Which = instanceId
        } });

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }
}
