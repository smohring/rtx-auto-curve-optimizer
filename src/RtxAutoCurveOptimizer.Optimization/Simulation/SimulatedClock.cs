namespace RtxAutoCurveOptimizer.Optimization.Simulation;

/// <summary>
/// Virtual <see cref="TimeProvider"/>: time only moves through <see cref="Advance"/> (called by the simulated stress
/// tester) or, while no load is running, by jumping to the due time of a newly created timer.
/// </summary>
public sealed class SimulatedClock : TimeProvider
{
    private readonly Lock gate = new();
    private readonly List<VirtualTimer> timers = [];
    private readonly DateTimeOffset origin;
    private long elapsedTicks;
    private int busy;
    private long sequence;

    public SimulatedClock(DateTimeOffset? start = null)
    {
        origin = start ?? new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    }

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (gate) return elapsedTicks;
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate) return origin + TimeSpan.FromTicks(elapsedTicks);
    }

    /// <summary>Virtual time elapsed since the start.</summary>
    public TimeSpan Elapsed
    {
        get
        {
            lock (gate) return TimeSpan.FromTicks(elapsedTicks);
        }
    }

    /// <summary>Number of armed timers.</summary>
    public int PendingTimerCount
    {
        get
        {
            lock (gate) return timers.Count(t => t.Armed);
        }
    }

    /// <summary>While busy, timers fire only through <see cref="Advance"/>.</summary>
    public void EnterBusy() => Interlocked.Increment(ref busy);

    public void ExitBusy() => Interlocked.Decrement(ref busy);

    /// <summary>Moves time forward and runs the callbacks of all timers that become due, in order and on this thread.</summary>
    public void Advance(TimeSpan span)
    {
        long target;
        lock (gate) target = elapsedTicks + span.Ticks;

        while (true)
        {
            VirtualTimer? next;
            lock (gate)
            {
                next = null;
                foreach (var t in timers)
                    if (t.Armed && t.DueTicks <= target && (next is null || t.DueTicks < next.DueTicks
                        || (t.DueTicks == next.DueTicks && t.Sequence < next.Sequence)))
                        next = t;

                if (next is null)
                {
                    elapsedTicks = Math.Max(elapsedTicks, target);
                    return;
                }

                elapsedTicks = Math.Max(elapsedTicks, next.DueTicks);
                next.AfterFire(elapsedTicks);
            }

            next.Invoke();
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new VirtualTimer(this, callback, state);
        lock (gate) timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    private void Remove(VirtualTimer timer)
    {
        lock (gate) timers.Remove(timer);
    }

    private bool IsIdle => Volatile.Read(ref busy) == 0;

    private sealed class VirtualTimer(SimulatedClock clock, TimerCallback callback, object? state) : ITimer
    {
        private long periodTicks;

        public bool Armed { get; private set; }

        public long DueTicks { get; private set; }

        public long Sequence { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            bool fireNow = false;
            lock (clock.gate)
            {
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    Armed = false;
                    return true;
                }

                Armed = true;
                DueTicks = clock.elapsedTicks + dueTime.Ticks;
                Sequence = ++clock.sequence;
                periodTicks = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;

                if (clock.IsIdle)
                {
                    clock.elapsedTicks = Math.Max(clock.elapsedTicks, DueTicks);
                    Armed = false;
                    fireNow = true;
                }
            }

            if (fireNow) ThreadPool.UnsafeQueueUserWorkItem(static t => t.Invoke(), this, preferLocal: false);
            return true;
        }

        public void AfterFire(long now)
        {
            if (periodTicks > 0)
            {
                DueTicks = now + periodTicks;
                Sequence = ++clock.sequence;
            }
            else
            {
                Armed = false;
            }
        }

        public void Invoke() => callback(state);

        public void Dispose()
        {
            Armed = false;
            clock.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
