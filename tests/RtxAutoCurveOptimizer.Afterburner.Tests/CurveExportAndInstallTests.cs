using System.Text.Json;
using RtxAutoCurveOptimizer.Core;

namespace RtxAutoCurveOptimizer.Afterburner.Tests;

public class CurveExportAndInstallTests
{
    private static readonly CurveOffsets Offsets = new([300_000, 150_000, -15_000]);

    [Fact]
    public void ToCsv_WritesHeaderAndOneRowPerPoint()
    {
        string csv = CurveExport.ToCsv(Samples.Stock(), Offsets);

        Assert.Equal(
            "index,voltage_mv,stock_mhz,offset_mhz,final_mhz\r\n"
            + "0,450,180,300,480\r\n"
            + "1,700,900,150,1050\r\n"
            + "2,1000,2722.5,-15,2707.5\r\n",
            csv);
    }

    [Fact]
    public void ToJson_ContainsPointsWithFinalFrequency()
    {
        using var doc = JsonDocument.Parse(CurveExport.ToJson(Samples.Stock(), Offsets));
        var points = doc.RootElement.GetProperty("points");

        Assert.Equal(3, points.GetArrayLength());
        var last = points[2];
        Assert.Equal(2, last.GetProperty("index").GetInt32());
        Assert.Equal(1000.0, last.GetProperty("voltageMv").GetDouble());
        Assert.Equal(2722.5, last.GetProperty("stockMhz").GetDouble());
        Assert.Equal(-15.0, last.GetProperty("offsetMhz").GetDouble());
        Assert.Equal(2707.5, last.GetProperty("finalMhz").GetDouble());
    }

    [Fact]
    public void Export_RejectsMismatchedOffsetCount()
    {
        Assert.Throws<ArgumentException>(() => CurveExport.ToCsv(Samples.Stock(), CurveOffsets.Zero(2)));
        Assert.Throws<ArgumentException>(() => CurveExport.ToJson(Samples.Stock(), CurveOffsets.Zero(4)));
    }

    [Fact]
    public void FindInstallDirectory_PrefersInstallDirAndAcceptsExePath()
    {
        using var temp = new TempProfiles();
        string exe = Path.Combine(temp.Directory, "MSIAfterburner.exe");

        Assert.Equal(temp.Directory, AfterburnerInstall.FindInstallDirectory(n => n == "InstallDir" ? temp.Directory : null));
        Assert.Equal(temp.Directory, AfterburnerInstall.FindInstallDirectory(n => n == "InstallPath" ? exe : null));
        Assert.Equal(Path.Combine(temp.Directory, "Profiles"),
            AfterburnerInstall.FindProfilesDirectory(n => n == "InstallPath" ? exe : null));
    }

    [Fact]
    public void FindInstallDirectory_FallsBackToDefaultWhenRegistryEntryIsUnusable()
    {
        Assert.Equal(AfterburnerInstall.DefaultDirectory, AfterburnerInstall.FindInstallDirectory(_ => null));
        Assert.Equal(AfterburnerInstall.DefaultDirectory,
            AfterburnerInstall.FindInstallDirectory(_ => @"Z:\does\not\exist"));
    }

    [Fact]
    public void IsRunning_DoesNotThrow()
    {
        _ = AfterburnerProcess.IsRunning();
    }
}
