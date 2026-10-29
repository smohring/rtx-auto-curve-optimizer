using RtxAutoCurveOptimizer.Core;

namespace RtxAutoCurveOptimizer.Stress;

/// <summary>One schedule segment: Duty is the load fraction per 100 ms window (0 = idle), Length its duration.</summary>
internal readonly record struct LoadPhase(double Duty, TimeSpan Length);

/// <summary>Generates the sequence of load phases for a load pattern; deterministic for a given seed.</summary>
internal sealed class LoadSchedule
{
    private readonly LoadPattern _pattern;
    private readonly double _intensity;
    private readonly Random _random;
    private readonly Queue<LoadPhase> _queue = new();

    public LoadSchedule(LoadPattern pattern, double intensity, int seed)
    {
        _pattern = pattern;
        _intensity = Math.Clamp(intensity, 0.1, 1.0);
        _random = new Random(seed);
    }

    /// <summary>Returns the next phase of the schedule.</summary>
    public LoadPhase Next()
    {
        if (_queue.Count == 0)
        {
            Refill();
        }

        return _queue.Dequeue();
    }

    private void Refill()
    {
        switch (_pattern)
        {
            case LoadPattern.Steady:
                _queue.Enqueue(new LoadPhase(_intensity, TimeSpan.FromHours(1)));
                break;
            case LoadPattern.Pulsed:
                AddPulse(400, 100);
                break;
            default:
                AddVariableCycle();
                break;
        }
    }

    // One load/idle pair with both lengths randomized by +-30 %.
    private void AddPulse(double loadMs, double idleMs)
    {
        _queue.Enqueue(new LoadPhase(_intensity, Jitter(loadMs)));
        _queue.Enqueue(new LoadPhase(0, Jitter(idleMs)));
    }

    // Steady stretch, a burst of short pulses, then a partial-duty stretch.
    private void AddVariableCycle()
    {
        _queue.Enqueue(new LoadPhase(_intensity, TimeSpan.FromMilliseconds(1500 + _random.Next(1500))));
        int pulses = 3 + _random.Next(3);
        for (int i = 0; i < pulses; i++)
        {
            AddPulse(150 + _random.Next(250), 50 + _random.Next(100));
        }

        double partial = _intensity * (0.3 + 0.5 * _random.NextDouble());
        _queue.Enqueue(new LoadPhase(Math.Max(partial, 0.1), TimeSpan.FromMilliseconds(1000 + _random.Next(1000))));
    }

    private TimeSpan Jitter(double ms) => TimeSpan.FromMilliseconds(ms * (0.7 + 0.6 * _random.NextDouble()));
}
