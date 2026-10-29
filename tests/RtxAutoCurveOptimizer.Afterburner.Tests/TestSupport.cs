using System.Text;
using RtxAutoCurveOptimizer.Core;

namespace RtxAutoCurveOptimizer.Afterburner.Tests;

/// <summary>Fixture access and a temp directory per test; nothing here touches Program Files.</summary>
internal sealed class TempProfiles : IDisposable
{
    public const string FixtureName = "VEN_10DE&DEV_2C02&SUBSYS_209510DE&REV_A1&BUS_1&DEV_0&FN_0.cfg";

    public TempProfiles()
    {
        Directory = Path.Combine(Path.GetTempPath(), "rtxaco-afterburner-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
    }

    public string Directory { get; }

    public static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", FixtureName);

    /// <summary>Copies the fixture into the temp directory (optionally under another name) and returns its path.</summary>
    public string CopyFixture(string? name = null)
    {
        string target = Path.Combine(Directory, name ?? FixtureName);
        File.Copy(FixturePath, target);
        return target;
    }

    /// <summary>Writes a file with the given text in Latin-1 (1:1 bytes).</summary>
    public string WriteText(string name, string text)
    {
        string target = Path.Combine(Directory, name);
        File.WriteAllBytes(target, Encoding.Latin1.GetBytes(text));
        return target;
    }

    public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
}

internal static class Samples
{
    /// <summary>Three-point stock curve: 450/700/1000 mV at 180/900/2722 MHz.</summary>
    public static StockCurve Stock() => new(
    [
        new CurvePoint(0, 450_000, 180_000),
        new CurvePoint(1, 700_000, 900_000),
        new CurvePoint(2, 1_000_000, 2_722_500),
    ]);

    public static GpuIdentity Gpu(uint deviceId, uint subsystemId = 0x209510DE, uint bus = 1) =>
        new("Test GPU", "621.01", deviceId, subsystemId, 0xA1, bus);
}
