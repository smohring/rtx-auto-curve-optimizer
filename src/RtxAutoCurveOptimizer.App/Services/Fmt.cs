using System.Globalization;
using RtxAutoCurveOptimizer.Core;

namespace RtxAutoCurveOptimizer.App.Services;

/// <summary>Culture-aware formatting of measurement values and durations.</summary>
public static class Fmt
{
    private static readonly ClockLimitReasons KnownReasons = Enum.GetValues<ClockLimitReasons>().Aggregate(ClockLimitReasons.None, (a, b) => a | b);

    public static string Mhz(double mhz) => string.Format(CultureInfo.CurrentCulture, "{0:0} MHz", mhz);

    public static string KhzAsMhz(int khz) => Mhz(khz / 1000.0);

    public static string Mv(double mv) => string.Format(CultureInfo.CurrentCulture, "{0:0} mV", mv);

    public static string UvAsMv(double uv) => Mv(uv / 1000.0);

    public static string Watts(double watts) => string.Format(CultureInfo.CurrentCulture, "{0:0.0} W", watts);

    public static string Celsius(double c) => string.Format(CultureInfo.CurrentCulture, "{0:0} °C", c);

    public static string Percent(double fraction) => string.Format(CultureInfo.CurrentCulture, "{0:0.0} %", fraction * 100);

    /// <summary>Power difference with sign, e.g. "-107 W".</summary>
    public static string SignedWatts(double watts) => string.Format(CultureInfo.CurrentCulture, "{0:+0;-0;0} W", watts);

    /// <summary>Localized duration such as "5 min 40 s".</summary>
    public static string Duration(TimeSpan span)
    {
        var total = (int)Math.Round(Math.Max(0, span.TotalSeconds));
        if (total < 60) return Loc.Format("Duration_Seconds", total);
        if (total < 3600) return Loc.Format("Duration_MinSec", total / 60, total % 60);
        return Loc.Format("Duration_HourMin", total / 3600, total % 3600 / 60);
    }

    /// <summary>Localized names of all set limit reasons; undefined NVML bits become one "Other (0x...)" entry.</summary>
    public static IReadOnlyList<string> LimitReasonNames(ClockLimitReasons reasons)
    {
        if (reasons == ClockLimitReasons.None) return [Loc.Get("Limit_None")];
        var parts = new List<string>();
        foreach (var flag in Enum.GetValues<ClockLimitReasons>())
            if (flag != ClockLimitReasons.None && reasons.HasFlag(flag))
                parts.Add(Loc.Get("Limit_" + flag));

        var unknown = (ulong)reasons & ~(ulong)KnownReasons;
        if (unknown != 0) parts.Add(Loc.Format("Limit_Other", "0x" + unknown.ToString("X", CultureInfo.InvariantCulture)));
        return parts;
    }

    /// <summary>Tile text: the first limit reason plus "+n" for the others.</summary>
    public static string LimitReasonsShort(IReadOnlyList<string> names) =>
        names.Count > 1 ? names[0] + " +" + (names.Count - 1).ToString(CultureInfo.CurrentCulture) : names[0];
}
