namespace RtxAutoCurveOptimizer.Optimization;

/// <summary>Search effort presets; see <see cref="OptimizerSettings.For"/>.</summary>
public enum OptimizationPreset { Quick, Standard, Thorough }

/// <summary>Anchors, test durations and search parameters of one optimization run.</summary>
public sealed record OptimizerSettings(
    IReadOnlyList<int> AnchorVoltagesMv,
    TimeSpan WarmupDuration, TimeSpan ReferenceDuration,
    TimeSpan SearchTestDuration, TimeSpan ConfirmTestDuration, TimeSpan ValidationDuration,
    int StepMhz = 15, int GallopStepMhz = 30, int SafetyMarginMhz = 30, int MaxOffsetMhz = 600,
    int ThermalDerateAllowanceMhz = 150, bool ColdConfirm = true)
{
    /// <summary>Tests per anchor assumed by <see cref="EstimateDuration"/>.</summary>
    public const int DefaultTestsPerAnchor = 8;

    /// <summary>Returns the settings of a preset.</summary>
    public static OptimizerSettings For(OptimizationPreset preset) => preset switch
    {
        OptimizationPreset.Quick => new OptimizerSettings(
            [850, 900, 950, 1000],
            Seconds(20), Seconds(10), Seconds(6), Seconds(20), Seconds(90)),
        OptimizationPreset.Standard => new OptimizerSettings(
            [850, 875, 900, 925, 950, 1000, 1050],
            Seconds(30), Seconds(15), Seconds(10), Seconds(45), Seconds(180)),
        OptimizationPreset.Thorough => new OptimizerSettings(
            [.. Enumerable.Range(0, 11).Select(i => 850 + 25 * i)],
            Seconds(45), Seconds(20), Seconds(20), Seconds(120), Seconds(600)),
        _ => throw new ArgumentOutOfRangeException(nameof(preset)),
    };

    /// <summary>Assumed time per anchor for the cold confirm: two cool-downs and two short bursts.</summary>
    public static readonly TimeSpan ColdConfirmEstimate = TimeSpan.FromSeconds(80);

    /// <summary>
    /// Estimated wall-clock time: warm-up, two references, per anchor N search tests, one confirm and the cold
    /// confirm, and validation.
    /// </summary>
    public TimeSpan EstimateDuration(int testsPerAnchor = DefaultTestsPerAnchor)
    {
        var perAnchor = SearchTestDuration * testsPerAnchor + ConfirmTestDuration + (ColdConfirm ? ColdConfirmEstimate : TimeSpan.Zero);
        return WarmupDuration + ReferenceDuration * 2 + perAnchor * AnchorVoltagesMv.Count + ValidationDuration;
    }

    /// <summary>Throws <see cref="ArgumentException"/> if the settings cannot drive a search.</summary>
    public void Validate()
    {
        if (AnchorVoltagesMv is null || AnchorVoltagesMv.Count == 0)
            throw new ArgumentException("At least one anchor voltage is required.", nameof(AnchorVoltagesMv));
        if (StepMhz <= 0) throw new ArgumentException("StepMhz must be positive.", nameof(StepMhz));
        if (GallopStepMhz < StepMhz) throw new ArgumentException("GallopStepMhz must be at least StepMhz.", nameof(GallopStepMhz));
        if (SafetyMarginMhz < 0) throw new ArgumentException("SafetyMarginMhz must not be negative.", nameof(SafetyMarginMhz));
        if (MaxOffsetMhz <= 0) throw new ArgumentException("MaxOffsetMhz must be positive.", nameof(MaxOffsetMhz));
        if (ThermalDerateAllowanceMhz < 0)
            throw new ArgumentException("ThermalDerateAllowanceMhz must not be negative.", nameof(ThermalDerateAllowanceMhz));
    }

    private static TimeSpan Seconds(int s) => TimeSpan.FromSeconds(s);
}
