using System.Diagnostics;
using RtxAutoCurveOptimizer.Core;
using RtxAutoCurveOptimizer.Optimization;

namespace RtxAutoCurveOptimizer.Cli;

/// <summary>Prints optimizer events as they arrive: phases, warnings, anchors and optionally every test.</summary>
internal sealed class ConsoleReporter(bool showTests, bool heartbeat) : IProgress<OptimizerEvent>
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    private readonly Lock gate = new();
    private readonly Stopwatch wallClock = Stopwatch.StartNew();
    private TimeSpan nextHeartbeat = HeartbeatInterval;
    private TimeSpan eta;

    public void Report(OptimizerEvent e)
    {
        lock (gate)
        {
            switch (e)
            {
                case PhaseChanged p:
                    Console.WriteLine($"== {p.Phase}");
                    break;
                case Warning w:
                    Console.WriteLine($"warning: {Describe(w)}");
                    break;
                case AnchorCompleted a:
                    var r = a.Result;
                    Console.WriteLine($"anchor {r.AnchorIndex}: {Shared.Mv(r.VoltageUv)} mV, max stable {Shared.Mhz(r.MaxStableKhz)} MHz, " +
                                      $"recommended {Shared.Mhz(r.RecommendedKhz)} MHz, {Shared.Fixed(r.AvgPowerW, 0)} W, perf {Shared.Fixed(r.ThroughputRelative * 100, 1)} %{(r.Limited ? " (limited)" : "")}");
                    break;
                case EtaUpdated t:
                    eta = t.Remaining;
                    break;
                case TestStarted s when showTests:
                    Console.WriteLine($"test: {s.Kind}{(s.AnchorIndex >= 0 ? $" anchor {s.AnchorIndex}" : "")} at {Shared.Mhz(s.FrequencyKhz)} MHz for {Shared.Duration(s.Duration)} (ETA {Shared.Duration(eta)})");
                    break;
                case TestFinished f when showTests:
                    var rec = f.Record;
                    var avg = rec.Telemetry;
                    Console.WriteLine($"  -> {rec.Outcome}{(rec.Outcome == StressOutcome.Passed && !rec.Valid ? " (operating point not reached)" : "")}" +
                                      (avg is null ? "" : $", {Shared.Fixed(avg.AvgClockMhz, 0)} MHz, {Shared.Fixed(avg.AvgVoltageUv / 1000.0, 0)} mV, {Shared.Fixed(avg.AvgPowerW, 0)} W, {Shared.Fixed(avg.AvgTemperatureC, 0)} C"));
                    break;
                case TelemetrySampled s when heartbeat && wallClock.Elapsed >= nextHeartbeat:
                    nextHeartbeat = wallClock.Elapsed + HeartbeatInterval;
                    var m = s.Telemetry;
                    Console.WriteLine($"  .. {m.GraphicsClockMhz} MHz, {Shared.Fixed(m.VoltageUv / 1000.0, 0)} mV, {Shared.Fixed(m.PowerW, 0)} W, {m.TemperatureC} C, limits {Shared.LimitText(m.LimitReasons)}");
                    break;
            }
        }
    }

    private static string Describe(Warning w) => w.Kind switch
    {
        WarningKind.DeviceLost => "device lost (driver reset); waiting, recalibrating and continuing",
        WarningKind.IntensityReduced => $"GPU missed the intended operating point under power/thermal limits; load intensity lowered to {w.Detail}",
        WarningKind.AnchorLimited => $"anchor {w.Detail} is limited (power/thermal capping)",
        WarningKind.AnchorSkipped => $"anchor {w.Detail} skipped (no usable result, or the flat test curve would exceed the +-1000 MHz offset range)",
        WarningKind.AnchorUnconfirmed => $"anchor {w.Detail} skipped (confirmation kept failing)",
        WarningKind.CurrentCurveUnstable => "the original curve failed the reference test",
        WarningKind.MarginIncreased => $"validation failed; extra margin raised to {w.Detail} MHz",
        WarningKind.ValidationFailed => "the recommended curve could not be validated",
        WarningKind.RestoreFailed => $"restoring the original offsets failed: {w.Detail}",
        WarningKind.ResumedFromSession => $"resumed session {w.Detail}",
        _ => w.Kind.ToString(),
    };
}

/// <summary>Prints the final summary of an optimization result.</summary>
internal static class ResultPrinter
{
    public static void Print(TextWriter w, OptimizationResult r)
    {
        w.WriteLine();
        w.WriteLine($"Result:   {r.Status}{(r.Failure != OptimizationFailure.None ? $" ({r.Failure}: {r.FailureDetail})" : r.FailureDetail is null ? "" : $" ({r.FailureDetail})")}");
        w.WriteLine($"Session:  {r.SessionId}");
        w.WriteLine($"Tests:    {r.Tests.Count} (search {r.Tests.Count(t => t.Kind == TestKind.Search)}, confirm {r.Tests.Count(t => t.Kind == TestKind.Confirm)}, " +
                    $"failed {r.Tests.Count(t => t.Outcome != StressOutcome.Passed)}), elapsed {Shared.Duration(r.Elapsed)}");
        PrintReference(w, "Stock reference", r.StockReference);
        PrintReference(w, "Original curve  ", r.CurrentReference);

        if (r.Anchors.Count > 0)
        {
            w.WriteLine();
            w.WriteLine("Anchors:");
            Shared.WriteTable(w, ["#", "mV", "stock MHz", "max stable", "recommended", "power W", "clock MHz", "temp C", "perf %", "perf/W", "note"],
                r.Anchors.Select(a => new[]
                {
                    a.AnchorIndex.ToString(), Shared.Mv(a.VoltageUv), Shared.Mhz(a.StockKhz), Shared.Mhz(a.MaxStableKhz), Shared.Mhz(a.RecommendedKhz),
                    Shared.Fixed(a.AvgPowerW, 1), Shared.Fixed(a.AvgClockMhz, 0), Shared.Fixed(a.AvgTemperatureC, 0),
                    Shared.Fixed(a.ThroughputRelative * 100, 1), Shared.Fixed(a.PerfPerWatt, 2),
                    a.Limited ? $"limited, load {Shared.Fixed(a.Intensity, 1)}" : "",
                }));
        }

        if (r.Proposals.Count > 0)
        {
            w.WriteLine();
            w.WriteLine("Proposals:");
            Shared.WriteTable(w, ["proposal", "cap mV", "cap MHz", "margin MHz", "power W", "perf %", "validated"],
                r.Proposals.Select(p => new[]
                {
                    p.Kind.ToString(), Shared.Mv(p.CapAnchor.VoltageUv), Shared.Mhz(p.CapAnchor.RecommendedKhz - p.ExtraMarginMhz * 1000),
                    p.ExtraMarginMhz.ToString(), Shared.Fixed(p.ExpectedPowerW, 1), Shared.Fixed(p.ExpectedRelativePerformance * 100, 1),
                    p.Validated ? "yes" : "no",
                }));
        }

        if (r.Recommended is { } rec)
        {
            w.WriteLine();
            w.WriteLine($"Recommended: {rec.Kind}, cap {Shared.Mv(rec.CapAnchor.VoltageUv)} mV at {Shared.Mhz(rec.CapAnchor.RecommendedKhz - rec.ExtraMarginMhz * 1000)} MHz, " +
                        $"about {Shared.Fixed(rec.ExpectedPowerW, 0)} W at {Shared.Fixed(rec.ExpectedRelativePerformance * 100, 1)} % of stock performance, " +
                        $"{(rec.Validated ? "validated" : "NOT validated")}");
        }

        w.WriteLine($"Original offsets restored: {(r.OffsetsRestored ? "yes" : "NO")}");
    }

    private static void PrintReference(TextWriter w, string label, ReferenceMeasurement? m)
    {
        if (m is null) return;
        var a = m.Averages;
        w.WriteLine($"{label}: {Shared.Fixed(a.AvgClockMhz, 0)} MHz, {Shared.Fixed(a.AvgVoltageUv / 1000.0, 0)} mV, {Shared.Fixed(a.AvgPowerW, 1)} W, throughput {m.Throughput:F0}/s");
    }
}
