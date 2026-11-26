namespace Retromind.Tests.TestInfrastructure;

/// <summary>Controllable debounce timers without sleeps or another test package.</summary>
internal sealed class ManualSearchTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _ticks;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        _timers.Add(timer); timer.Change(dueTime, period); return timer;
    }
    internal void Advance(int milliseconds)
    {
        _ticks += TimeSpan.FromMilliseconds(milliseconds).Ticks;
        foreach (var timer in _timers.Where(t => t.Due <= _ticks).ToArray()) timer.Fire();
    }
    private sealed class ManualTimer(ManualSearchTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        internal long Due = long.MaxValue;
        private TimeSpan _period;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        { _period = period; Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._ticks + dueTime.Ticks; return true; }
        internal void Fire() { Due = _period > TimeSpan.Zero ? owner._ticks + _period.Ticks : long.MaxValue; callback(state); }
        public void Dispose() => owner._timers.Remove(this);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
