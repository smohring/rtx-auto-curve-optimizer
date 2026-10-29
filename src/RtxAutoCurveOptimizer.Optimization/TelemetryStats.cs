using RtxAutoCurveOptimizer.Core;

namespace RtxAutoCurveOptimizer.Optimization;

/// <summary>Summaries and validity checks over the telemetry samples of one test.</summary>
internal static class TelemetryStats
{
    public const double ClockAboveToleranceMhz = 30;
    public const double VoltageToleranceUv = 12_500;
    private const int LoadedUtilizationPercent = 50;

    /// <summary>Averages and medians of the loaded samples (all samples if none was loaded).</summary>
    public static TelemetryAverages Summarize(IReadOnlyList<Telemetry> samples)
    {
        var reasons = ClockLimitReasons.None;
        foreach (var s in samples) reasons |= s.LimitReasons;

        var loaded = samples.Where(s => s.UtilizationPercent >= LoadedUtilizationPercent).ToList();
        if (loaded.Count == 0) loaded = [.. samples];
        if (loaded.Count == 0)
            return new TelemetryAverages(0, 0, 0, 0, 0, 0, 0, reasons);

        return new TelemetryAverages(
            loaded.Count,
            loaded.Average(s => s.GraphicsClockMhz), loaded.Average(s => s.VoltageUv),
            loaded.Average(s => s.PowerW), loaded.Average(s => s.TemperatureC),
            Median(loaded.Select(s => (double)s.GraphicsClockMhz)), Median(loaded.Select(s => (double)s.VoltageUv)),
            reasons, loaded.Max(s => s.GraphicsClockMhz));
    }

    /// <summary>
    /// True if the median voltage is on the anchor and the median clock is at most <see cref="ClockAboveToleranceMhz"/>
    /// above and at most <paramref name="thermalDerateAllowanceMhz"/> below the target frequency.
    /// </summary>
    public static bool IsValid(TelemetryAverages averages, int frequencyKhz, int anchorVoltageUv, int thermalDerateAllowanceMhz)
    {
        var clockOffsetMhz = averages.MedianClockMhz - frequencyKhz / 1000.0;
        return averages.SampleCount > 0
               && Math.Abs(averages.MedianVoltageUv - anchorVoltageUv) <= VoltageToleranceUv
               && clockOffsetMhz <= ClockAboveToleranceMhz
               && clockOffsetMhz >= -thermalDerateAllowanceMhz;
    }

    /// <summary>True if the median voltage is below the anchor tolerance (the GPU dropped to a lower point).</summary>
    public static bool VoltageBelowAnchor(TelemetryAverages averages, int anchorVoltageUv) =>
        averages.SampleCount > 0 && averages.MedianVoltageUv < anchorVoltageUv - VoltageToleranceUv;

    /// <summary>True if the median voltage is above the anchor tolerance (the test curve is not flat as intended).</summary>
    public static bool VoltageAboveAnchor(TelemetryAverages averages, int anchorVoltageUv) =>
        averages.SampleCount > 0 && averages.MedianVoltageUv > anchorVoltageUv + VoltageToleranceUv;

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
