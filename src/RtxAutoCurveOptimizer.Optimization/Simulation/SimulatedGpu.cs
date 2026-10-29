using RtxAutoCurveOptimizer.Core;

namespace RtxAutoCurveOptimizer.Optimization.Simulation;

/// <summary>Configuration of <see cref="SimulatedGpu"/>: hidden stability truth, power model and driver limits.</summary>
public sealed class SimulatedGpuOptions
{
    /// <summary>Stable delta above stock (MHz) at and below 850 mV.</summary>
    public double DeltaAt850MvMhz { get; init; } = 280;

    /// <summary>Stable delta above stock (MHz) at and above 1050 mV; linear in between.</summary>
    public double DeltaAt1050MvMhz { get; init; } = 200;

    /// <summary>Overrides the delta model: voltage in mV to stable delta in MHz.</summary>
    public Func<int, double>? TrueDeltaMhz { get; init; }

    /// <summary>Dynamic power in W per GHz and V squared at full duty.</summary>
    public double PowerCoefficient { get; init; } = 90;

    public double StaticPowerW { get; init; } = 20;

    /// <summary>Power limit that lowers the clock when exceeded; null disables capping.</summary>
    public double? PowerCapW { get; init; } = 360;

    /// <summary>Power limit reported in telemetry.</summary>
    public double PowerLimitW { get; init; } = 360;

    /// <summary>Offsets are clamped to this magnitude in MHz, like the driver range in the spec; null means no limit.</summary>
    public int? OffsetLimitMhz { get; init; }

    /// <summary>Offsets present before the optimizer starts, in kHz; null means zero.</summary>
    public int InitialOffsetKhz { get; init; }

    public bool CanWrite { get; init; } = true;

    /// <summary>A device reset (TDR) drops the applied offsets, as a driver reset may.</summary>
    public bool ResetOffsetsOnDeviceLost { get; init; } = true;

    /// <summary>Thermal clock bins: under load the reported clock sits this many MHz below the curve frequency, voltage unchanged.</summary>
    public double ThermalDerateMhz { get; init; }

    /// <summary>
    /// Enables the heat model: the GPU heats up under load and cools when idle with this time constant; the derate
    /// and <see cref="HotLimitPenaltyMhz"/> scale with the heat (0 cold, 1 hot). Null keeps a constant derate.
    /// </summary>
    public TimeSpan? HeatTimeConstant { get; init; }

    /// <summary>With the heat model: how much lower the true stable limit is when fully hot, in MHz.</summary>
    public double HotLimitPenaltyMhz { get; init; }

    /// <summary>Reports SwPowerCap and SwThermalSlowdown under load even when the GPU is not capped.</summary>
    public bool SpuriousCapReasons { get; init; }

    /// <summary>
    /// After a device reset the handles go stale, as on the real driver: telemetry throws and curve writes are
    /// silently ignored until <see cref="SimulatedGpu.Reinitialize"/> is called.
    /// </summary>
    public bool StaleHandlesAfterDeviceLost { get; init; }

    /// <summary>Reinitialize throws, i.e. the GPU stays unreachable.</summary>
    public bool ReinitializeFails { get; init; }
}

/// <summary>Where the simulated GPU runs under load: curve point, clock, voltage and power.</summary>
public readonly record struct SimulatedOperatingPoint(
    int PointIndex, int ClockKhz, int VoltageUv, double PowerW, bool PowerCapped);

/// <summary>
/// Simulated GPU with an RTX 5080-like stock curve. Under load it runs at the highest-frequency point that fits
/// the power cap (lowest voltage on ties), which is the lowest-voltage point reaching the curve maximum without a cap.
/// </summary>
public sealed class SimulatedGpu : IGpuControl
{
    private readonly Lock gate = new();
    private readonly SimulatedGpuOptions options;
    private readonly TimeProvider time;
    private readonly int seed;
    private int[] offsetsKhz;
    private LoadState? load;
    private bool stale;
    private double heat;
    private DateTimeOffset heatAt;

    private sealed record LoadState(LoadPattern Pattern, double Intensity);

    public SimulatedGpu(TimeProvider time, SimulatedGpuOptions? options = null, int seed = 1)
    {
        this.time = time;
        this.options = options ?? new SimulatedGpuOptions();
        this.seed = seed;
        Stock = CreateRtx5080StockCurve();
        offsetsKhz = [.. Enumerable.Repeat(this.options.InitialOffsetKhz, Stock.Points.Count)];
        Identity = new GpuIdentity("Simulated GeForce RTX 5080", "621.01", 0x2C02, 0x209510DE, 0xA1, 1);
        heatAt = time.GetUtcNow();
    }

    /// <summary>True if the heat model is enabled (<see cref="SimulatedGpuOptions.HeatTimeConstant"/>).</summary>
    public bool HasHeatModel => options.HeatTimeConstant is not null;

    /// <summary>Current heat, 0 (cold) to 1 (hot); always 1 without the heat model.</summary>
    public double Heat
    {
        get
        {
            lock (gate) return UpdateHeat();
        }
    }

    /// <summary>MHz the clock currently sits below the curve value.</summary>
    public double CurrentDerateMhz => options.ThermalDerateMhz * Heat;

    /// <summary>The true stable limit of a point at the current heat, in kHz.</summary>
    public int CurrentLimitKhz(int pointIndex) => TrueLimitKhz(pointIndex) - (int)Math.Round(options.HotLimitPenaltyMhz * Heat * 1000);

    public StockCurve Stock { get; }

    public GpuIdentity Identity { get; }

    public bool CanWrite => options.CanWrite;

    /// <summary>Number of successful ApplyOffsets calls.</summary>
    public int ApplyCount { get; private set; }

    /// <summary>Number of Reinitialize calls.</summary>
    public int ReinitializeCount { get; private set; }

    public StockCurve ReadStockCurve() => Stock;

    public CurveOffsets ReadOffsets()
    {
        lock (gate) return new CurveOffsets([.. offsetsKhz]);
    }

    public void ApplyOffsets(CurveOffsets offsets)
    {
        if (!options.CanWrite) throw new GpuAccessDeniedException("The simulated process is not elevated.");
        if (offsets.OffsetsKhz.Count != Stock.Points.Count)
            throw new GpuControlException("Offset count does not match the curve.");
        var limitKhz = (options.OffsetLimitMhz ?? int.MaxValue / 1000) * 1000;
        lock (gate)
        {
            if (stale) return;
            offsetsKhz = [.. offsets.OffsetsKhz.Select(o => Math.Clamp(o, -limitKhz, limitKhz))];
            ApplyCount++;
        }
    }

    public Telemetry ReadTelemetry()
    {
        LoadState? current;
        lock (gate)
        {
            if (stale) throw new GpuControlException("Simulated stale NVML handle.");
            current = load;
        }
        var now = time.GetUtcNow();
        var noise = Noise(now.UtcTicks);

        if (current is null)
            return new Telemetry(now, 210, 405, 650_000, options.StaticPowerW + 2, options.PowerLimitW,
                HasHeatModel ? (int)Math.Round(34 + 33 * Heat) : 34, 0, ClockLimitReasons.GpuIdle);

        var op = OperatingPoint(current.Pattern, current.Intensity);
        var duty = Duty(current.Pattern, current.Intensity);
        var power = op.PowerW * (1 + 0.01 * noise);
        var heatNow = Heat;
        var temperature = HasHeatModel ? 34 + (0.12 * power - 2) * heatNow + noise : 32 + 0.12 * power + noise;
        var reasons = op.PowerCapped || options.SpuriousCapReasons
            ? ClockLimitReasons.SwPowerCap | (options.SpuriousCapReasons ? ClockLimitReasons.SwThermalSlowdown : 0)
            : ClockLimitReasons.None;
        return new Telemetry(
            now, (int)Math.Round(op.ClockKhz / 1000.0 - options.ThermalDerateMhz * heatNow), 14_000, op.VoltageUv, power,
            options.PowerLimitW, (int)Math.Round(temperature), (int)Math.Round(100 * duty), reasons);
    }

    public void Reinitialize()
    {
        lock (gate)
        {
            ReinitializeCount++;
            if (options.ReinitializeFails) throw new GpuControlException("Simulated GPU did not come back.");
            stale = false;
        }
    }

    public void Dispose()
    {
    }

    /// <summary>Marks the GPU as loaded; called by the simulated stress tester.</summary>
    internal void SetLoad(LoadPattern pattern, double intensity)
    {
        lock (gate)
        {
            UpdateHeat();
            load = new LoadState(pattern, intensity);
        }
    }

    internal void ClearLoad()
    {
        lock (gate)
        {
            UpdateHeat();
            load = null;
        }
    }

    /// <summary>Moves the heat towards 1 under load and 0 when idle; call under the lock.</summary>
    private double UpdateHeat()
    {
        if (options.HeatTimeConstant is not { } tau) return 1;
        var now = time.GetUtcNow();
        var dt = (now - heatAt).TotalSeconds;
        heatAt = now;
        var target = load is null ? 0 : 1;
        if (dt > 0) heat = target + (heat - target) * Math.Exp(-dt / tau.TotalSeconds);
        return heat;
    }

    /// <summary>Applies a device reset: optionally drops the offsets and ends the load.</summary>
    internal void ResetDevice()
    {
        lock (gate)
        {
            UpdateHeat();
            load = null;
            if (options.ResetOffsetsOnDeviceLost) offsetsKhz = new int[Stock.Points.Count];
            stale |= options.StaleHandlesAfterDeviceLost;
        }
    }

    /// <summary>The hidden true maximum stable frequency of a curve point, in kHz.</summary>
    public int TrueLimitKhz(int pointIndex)
    {
        var point = Stock.Points[pointIndex];
        return point.StockKhz + (int)Math.Round(TrueDelta(point.VoltageUv / 1000.0) * 1000);
    }

    /// <summary>Operating point under the given load with the currently applied curve.</summary>
    public SimulatedOperatingPoint OperatingPoint(LoadPattern pattern, double intensity)
    {
        int[] offsets;
        lock (gate) offsets = offsetsKhz;
        var duty = Duty(pattern, intensity);
        var points = Stock.Points;

        var best = -1;
        var bestFrequency = int.MinValue;
        var unconstrained = 0;
        var maxFrequency = int.MinValue;
        for (var j = 0; j < points.Count; j++)
        {
            var frequency = points[j].StockKhz + offsets[j];
            if (frequency > maxFrequency)
            {
                maxFrequency = frequency;
                unconstrained = j;
            }

            var fits = options.PowerCapW is not { } cap || PowerAt(frequency, points[j].VoltageUv, duty) <= cap;
            if (fits && frequency > bestFrequency)
            {
                bestFrequency = frequency;
                best = j;
            }
        }

        if (best < 0) best = 0;
        var clock = points[best].StockKhz + offsets[best];
        return new SimulatedOperatingPoint(
            best, clock, points[best].VoltageUv, PowerAt(clock, points[best].VoltageUv, duty), best != unconstrained);
    }

    /// <summary>Fraction of time the GPU is busy for a pattern at an intensity.</summary>
    public static double Duty(LoadPattern pattern, double intensity) => intensity * pattern switch
    {
        LoadPattern.Steady => 1.0,
        LoadPattern.Pulsed => 0.8,
        _ => 0.7,
    };

    private double PowerAt(int frequencyKhz, int voltageUv, double duty)
    {
        var v = voltageUv / 1e6;
        return options.StaticPowerW + options.PowerCoefficient * (frequencyKhz / 1e6) * v * v * duty;
    }

    private double TrueDelta(double voltageMv)
    {
        if (options.TrueDeltaMhz is { } custom) return custom((int)Math.Round(voltageMv));
        if (voltageMv <= 850) return options.DeltaAt850MvMhz;
        if (voltageMv >= 1050) return options.DeltaAt1050MvMhz;
        return options.DeltaAt850MvMhz
               + (options.DeltaAt1050MvMhz - options.DeltaAt850MvMhz) * (voltageMv - 850) / 200;
    }

    /// <summary>Deterministic noise in [-1, 1] derived from the time and the seed.</summary>
    private double Noise(long ticks)
    {
        var x = unchecked((ulong)ticks ^ ((ulong)(uint)seed * 0x9E3779B97F4A7C15UL));
        x ^= x >> 33;
        x *= 0xFF51AFD7ED558CCDUL;
        x ^= x >> 33;
        return (x % 2001) / 1000.0 - 1;
    }

    /// <summary>127 graphics points, 450-1240 mV (5 mV steps up to 920 mV, then 10 mV), RTX 5080-like frequencies.</summary>
    public static StockCurve CreateRtx5080StockCurve()
    {
        (double Mv, double Mhz)[] anchors =
        [
            (450, 180), (750, 180), (790, 967), (825, 1755), (845, 2107), (850, 2137), (900, 2355), (950, 2550),
            (1000, 2722), (1050, 2850), (1100, 2947), (1200, 3112), (1240, 3165),
        ];

        var voltages = Enumerable.Range(0, 95).Select(i => 450 + 5 * i)
            .Concat(Enumerable.Range(0, 32).Select(i => 930 + 10 * i)).ToArray();
        var points = new List<CurvePoint>(voltages.Length);
        for (var i = 0; i < voltages.Length; i++)
        {
            var mv = (double)voltages[i];
            var k = Array.FindIndex(anchors, a => a.Mv >= mv);
            var (mv0, f0) = k <= 0 ? anchors[0] : anchors[k - 1];
            var (mv1, f1) = anchors[Math.Max(k, 0)];
            var mhz = mv1 <= mv0 ? f1 : f0 + (f1 - f0) * (mv - mv0) / (mv1 - mv0);
            points.Add(new CurvePoint(i, voltages[i] * 1000, (int)(Math.Round(mhz / 7.5) * 7500)));
        }

        return new StockCurve(points);
    }
}
