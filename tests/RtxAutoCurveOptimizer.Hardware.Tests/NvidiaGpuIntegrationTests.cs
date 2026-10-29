using System.Runtime.InteropServices;
using RtxAutoCurveOptimizer.Core;
using Xunit.Abstractions;

namespace RtxAutoCurveOptimizer.Hardware.Tests;

/// <summary>Read-only tests against the real GPU; they return early when no NVIDIA driver is installed.</summary>
public class NvidiaGpuIntegrationTests(ITestOutputHelper output)
{
    private static bool DriverPresent() =>
        NativeLibrary.TryLoad("nvapi64.dll", out var h) && Free(h);

    private static bool Free(nint handle)
    {
        NativeLibrary.Free(handle);
        return true;
    }

    [Fact]
    public void RealGpu_ReadsIdentityCurveOffsetsAndTelemetry()
    {
        if (!DriverPresent()) return;
        using var gpu = NvidiaGpu.OpenFirst();

        var id = gpu.Identity;
        output.WriteLine($"Identity: {id}");
        Assert.Contains("NVIDIA", id.Name, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(@"^\d+\.\d{2}$", id.DriverVersion);
        Assert.NotEqual(0u, id.DeviceId);
        Assert.True(id.DeviceId <= 0xFFFF);

        var curve = gpu.ReadStockCurve();
        var points = curve.Points;
        output.WriteLine($"Points: {points.Count}, first {points[0]}, last {points[^1]}");
        Assert.Equal(127, points.Count);
        Assert.InRange(points[0].VoltageUv, 400_000, 500_000);
        Assert.InRange(points[^1].VoltageUv, 1_200_000, 1_300_000);
        for (var i = 1; i < points.Count; i++)
            Assert.True(points[i].VoltageUv >= points[i - 1].VoltageUv);
        Assert.All(points, p => Assert.True(p.StockKhz > 0));

        var offsets = gpu.ReadOffsets();
        output.WriteLine("Offsets (kHz) at indices 0, 40, 80, 126: " +
            string.Join(", ", new[] { 0, 40, 80, 126 }.Select(i => offsets.OffsetsKhz[i])));
        Assert.Equal(points.Count, offsets.OffsetsKhz.Count);

        var t = gpu.ReadTelemetry();
        output.WriteLine($"Telemetry: {t}");
        Assert.True(t.VoltageUv > 0);
        Assert.True(t.GraphicsClockMhz > 0);
        Assert.True(t.PowerW > 0);
        Assert.True(t.PowerLimitW > 0);
        Assert.InRange(t.TemperatureC, 1, 120);
    }

    [Fact]
    public async Task RealGpu_TelemetryIsThreadSafe()
    {
        if (!DriverPresent()) return;
        using var gpu = NvidiaGpu.OpenFirst();

        var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 25; i++)
            {
                Assert.True(gpu.ReadTelemetry().VoltageUv > 0);
                Assert.Equal(127, gpu.ReadStockCurve().Points.Count);
            }
        })).ToArray();

        await Task.WhenAll(tasks);
    }

    [Fact]
    public void RealGpu_ApplyOffsetsWithCurrentOffsets_ThrowsAccessDeniedWhenNotElevated()
    {
        if (!DriverPresent()) return;
        using var gpu = NvidiaGpu.OpenFirst();
        if (gpu.CanWrite) return; // elevated: this call would really write, so never run it

        var current = gpu.ReadOffsets();

        var ex = Assert.Throws<GpuAccessDeniedException>(() => gpu.ApplyOffsets(current));
        Assert.Equal(-137, ex.NvapiStatus);
        Assert.Contains("ClkVfPointsSetControl", ex.Message);
        Assert.Equal(current.OffsetsKhz, gpu.ReadOffsets().OffsetsKhz);
    }

    [Fact]
    public void RealGpu_ApplyOffsetsWithWrongCount_Throws()
    {
        if (!DriverPresent()) return;
        using var gpu = NvidiaGpu.OpenFirst();

        Assert.Throws<ArgumentException>(() => gpu.ApplyOffsets(CurveOffsets.Zero(5)));
    }
}
