using RtxAutoCurveOptimizer.Core;

namespace RtxAutoCurveOptimizer.Afterburner.Tests;

public class AfterburnerVfCurveTests
{
    private static AfterburnerProfile LoadFixture() =>
        new AfterburnerProfileStore(Path.GetDirectoryName(TempProfiles.FixturePath)!).Read(TempProfiles.FixturePath);

    [Fact]
    public void EveryCurveInFixture_RoundTripsExactly()
    {
        var profile = LoadFixture();
        int checkedCurves = 0;
        foreach (string section in profile.SectionNames)
        {
            string? hex = profile.GetValue(section, "VFCurve");
            if (string.IsNullOrEmpty(hex))
                continue;
            Assert.Equal(hex, AfterburnerVfCurve.Parse(hex).ToHex());
            checkedCurves++;
        }
        Assert.True(checkedCurves >= 3, "Expected at least Profile1, Startup and Defaults.");
    }

    [Fact]
    public void Defaults_DecodesKnownPoints()
    {
        var curve = LoadFixture().GetCurve("Defaults")!;

        Assert.Equal(127, curve.Points.Count);
        Assert.Equal(3224, curve.LengthBytes);
        Assert.Equal(new VfCurvePoint(450f, 180f, 0f), curve.Points[0]);
        Assert.Equal(new VfCurvePoint(1000f, 2722f, 0f), curve.Points[88]);
        Assert.Equal(new VfCurvePoint(1240f, 3165f, 0f), curve.Points[^1]);
        Assert.All(curve.Points, p => Assert.Equal(0f, p.OffsetMhz));
    }

    [Fact]
    public void Startup_HasSamePointsWithOffset300()
    {
        var profile = LoadFixture();
        var defaults = profile.GetCurve("Defaults")!;
        var startup = profile.GetCurve("Startup")!;

        Assert.Equal(127, startup.Points.Count);
        for (int i = 0; i < 127; i++)
        {
            Assert.Equal(defaults.Points[i].VoltageMv, startup.Points[i].VoltageMv);
            Assert.Equal(defaults.Points[i].StockMhz, startup.Points[i].StockMhz);
            Assert.Equal(300f, startup.Points[i].OffsetMhz);
        }
        Assert.Equal(3022f, startup.Points[88].FinalMhz);
    }

    [Fact]
    public void Create_ConvertsUnitsAndPadsToDefaultLength()
    {
        var offsets = new CurveOffsets([300_000, 150_000, -15_000]);

        var curve = AfterburnerVfCurve.Create(Samples.Stock(), offsets);

        Assert.Equal(AfterburnerVfCurve.DefaultLengthBytes, curve.LengthBytes);
        Assert.Equal(new VfCurvePoint(450f, 180f, 300f), curve.Points[0]);
        Assert.Equal(new VfCurvePoint(1000f, 2722.5f, -15f), curve.Points[2]);
        string hex = curve.ToHex();
        Assert.Equal(AfterburnerVfCurve.DefaultLengthBytes * 2, hex.Length);
        Assert.StartsWith("00000200" + "03000000" + "00000000", hex);
        Assert.Equal(hex, AfterburnerVfCurve.Parse(hex).ToHex());
    }

    [Fact]
    public void Create_KeepsRequestedLength()
    {
        var curve = AfterburnerVfCurve.Create(Samples.Stock(), CurveOffsets.Zero(3), lengthBytes: 100);

        Assert.Equal(100, curve.LengthBytes);
        Assert.Equal(200, curve.ToHex().Length);
    }

    [Fact]
    public void Create_RejectsMismatchedOrTooSmallInput()
    {
        Assert.Throws<ArgumentException>(() => AfterburnerVfCurve.Create(Samples.Stock(), CurveOffsets.Zero(2)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AfterburnerVfCurve.Create(Samples.Stock(), CurveOffsets.Zero(3), lengthBytes: 20));
    }

    [Fact]
    public void Parse_AcceptsLowercaseAndWritesUppercase()
    {
        string upper = AfterburnerVfCurve.Create(Samples.Stock(), CurveOffsets.Zero(3)).ToHex();

        Assert.Equal(upper, AfterburnerVfCurve.Parse(upper.ToLowerInvariant()).ToHex());
    }

    [Theory]
    [InlineData("")]
    [InlineData("ZZ")]
    [InlineData("000")]
    [InlineData("0000020005000000")]
    [InlineData("0100000001000000000000000000000000000000000000000000000000")]
    [InlineData("000002000500000000000000")]
    public void Parse_RejectsInvalidBlobs(string hex)
    {
        Assert.Throws<FormatException>(() => AfterburnerVfCurve.Parse(hex));
        Assert.False(AfterburnerVfCurve.TryParse(hex, out var curve));
        Assert.Null(curve);
    }

    [Fact]
    public void Parse_PreservesNonZeroPaddingAndHeaderWord()
    {
        string hex = "00000200" + "01000000" + "07000000"
            + "0000E143" + "00003443" + "00000000"
            + "DEADBEEF";

        Assert.Equal(hex, AfterburnerVfCurve.Parse(hex).ToHex());
    }
}

public sealed class AfterburnerOffsetsTests
{
    private static AfterburnerProfile Fixture() => new AfterburnerProfileStore(Path.GetDirectoryName(TempProfiles.FixturePath)!).Read(TempProfiles.FixturePath);

    private static StockCurve StockFrom(AfterburnerVfCurve curve) =>
        new([.. curve.Points.Select((p, i) => new CurvePoint(i, (int)Math.Round(p.VoltageMv * 1000), (int)Math.Round(p.StockMhz * 1000)))]);

    [Fact]
    public void StartupCurve_GivesItsOffsetsForAMatchingStockCurve()
    {
        var profile = Fixture();
        var stock = StockFrom(profile.GetCurve("Defaults")!);

        var offsets = profile.GetCurve("Startup")!.ToOffsets(stock);

        Assert.NotNull(offsets);
        Assert.Equal(127, offsets.OffsetsKhz.Count);
        Assert.All(offsets.OffsetsKhz, o => Assert.Equal(300_000, o));
    }

    [Fact]
    public void CurveForAnotherGpu_GivesNull()
    {
        var profile = Fixture();
        var stock = StockFrom(profile.GetCurve("Defaults")!);
        var shorter = new StockCurve([.. stock.Points.Take(100)]);
        var shifted = new StockCurve([.. stock.Points.Select(p => p with { VoltageUv = p.VoltageUv + 10_000 })]);

        Assert.Null(profile.GetCurve("Startup")!.ToOffsets(shorter));
        Assert.Null(profile.GetCurve("Startup")!.ToOffsets(shifted));
    }
}
