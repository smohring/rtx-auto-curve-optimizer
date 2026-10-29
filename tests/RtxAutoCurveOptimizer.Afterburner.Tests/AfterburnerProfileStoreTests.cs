using System.Text;
using RtxAutoCurveOptimizer.Core;

namespace RtxAutoCurveOptimizer.Afterburner.Tests;

public class AfterburnerProfileStoreTests : IDisposable
{
    private readonly TempProfiles _temp = new();

    public void Dispose() => _temp.Dispose();

    private static string Text(string path) => Encoding.Latin1.GetString(File.ReadAllBytes(path));

    private static AfterburnerVfCurve SampleCurve() =>
        AfterburnerVfCurve.Create(Samples.Stock(), new CurveOffsets([200_000, 150_000, 105_000]));

    [Fact]
    public void FindProfileFile_PlainAndNvapiDeviceIdForms()
    {
        string file = _temp.CopyFixture();
        var store = new AfterburnerProfileStore(_temp.Directory);

        Assert.Equal(file, store.FindProfileFile(Samples.Gpu(0x2C02)));
        Assert.Equal(file, store.FindProfileFile(Samples.Gpu(0x2C0210DE)));
    }

    [Fact]
    public void FindProfileFile_ReturnsNullWhenNothingMatches()
    {
        _temp.CopyFixture();
        _temp.WriteText("VEN_0000&DEV_0000&SUBSYS_00000000&REV_00&BUS_0&DEV_0&FN_0.cfg", "[Startup]\r\n");
        var store = new AfterburnerProfileStore(_temp.Directory);

        Assert.Null(store.FindProfileFile(Samples.Gpu(0x2C02, subsystemId: 0x12345678)));
        Assert.Null(store.FindProfileFile(Samples.Gpu(0x2B85)));
        Assert.Null(new AfterburnerProfileStore(Path.Combine(_temp.Directory, "missing")).FindProfileFile(Samples.Gpu(0x2C02)));
    }

    [Fact]
    public void FindProfileFile_PicksFileOfTheGivenBusAndIgnoresBackups()
    {
        string bus1 = _temp.CopyFixture();
        string bus2 = _temp.WriteText("VEN_10DE&DEV_2C02&SUBSYS_209510DE&REV_A1&BUS_2&DEV_0&FN_0.cfg", "[Startup]\r\n");
        _temp.WriteText("VEN_10DE&DEV_2C02&SUBSYS_209510DE&REV_A1&BUS_1&DEV_0&FN_0.cfg.20260101-000000.bak", "x");
        var store = new AfterburnerProfileStore(_temp.Directory);

        Assert.Equal(bus1, store.FindProfileFile(Samples.Gpu(0x2C0210DE, bus: 1)));
        Assert.Equal(bus2, store.FindProfileFile(Samples.Gpu(0x2C0210DE, bus: 2)));
    }

    [Fact]
    public void Read_FixtureReportsSlotsAndKeys()
    {
        var profile = new AfterburnerProfileStore(_temp.Directory).Read(_temp.CopyFixture());

        Assert.Equal(["Profile1", "Startup", "Defaults", "Settings", "PreSuspendedMode"], profile.SectionNames);
        Assert.Equal([2, 3, 4, 5], profile.EmptySlots);
        Assert.False(profile.IsSlotEmpty(1));
        Assert.Equal(300_000, profile.GetInt("Startup", "CoreClkBoost"));
        Assert.Equal(108, profile.GetInt("Startup", "PowerLimit"));
        Assert.Equal("2", profile.GetValue("Startup", "Format"));
        Assert.Null(profile.GetValue("Profile3", "VFCurve"));
        Assert.Null(profile.GetCurve("PreSuspendedMode"));
    }

    [Fact]
    public void EmptySlots_TreatsMissingKeyAndEmptyCurveAsEmpty()
    {
        string path = _temp.WriteText("p.cfg",
            "[Profile1]\r\nVFCurve=\r\n[Profile2]\r\nFormat=2\r\n[Profile4]\r\nVFCurve=00000200\r\n[Startup]\r\nFormat=2\r\n");

        var profile = new AfterburnerProfileStore(_temp.Directory).Read(path);

        Assert.Equal([1, 2, 3, 5], profile.EmptySlots);
    }

    [Fact]
    public void WriteProfile_NewSlotChangesOnlyThatSection()
    {
        string path = _temp.CopyFixture();
        byte[] original = File.ReadAllBytes(path);
        var store = new AfterburnerProfileStore(_temp.Directory);
        var curve = SampleCurve();

        string backup = store.WriteProfile(path, 3, curve, coreClkBoostKhz: 105_000);

        Assert.Equal(original, File.ReadAllBytes(backup));
        Assert.EndsWith(".bak", backup);
        Assert.StartsWith(path, backup);

        string before = Encoding.Latin1.GetString(original);
        string after = Text(path);
        int start = after.IndexOf("[Profile3]", StringComparison.Ordinal);
        int end = after.IndexOf("[Startup]", StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);
        Assert.Equal(before, after.Remove(start, end - start));

        string expectedSection =
            "[Profile3]\r\nFormat=2\r\nPowerLimit=108\r\nCoreClkBoost=105000\r\n"
            + "VFCurve=" + curve.ToHex() + "\r\n"
            + "MemClkBoost=2000000\r\nFanMode=1\r\nFanSpeed=30\r\nFanMode2=\r\nFanSpeed2=\r\nCoreVoltageBoost=0\r\n";
        Assert.Equal(expectedSection, after[start..end]);
    }

    [Fact]
    public void WriteProfile_ResultIsReadableAndSlotNoLongerEmpty()
    {
        string path = _temp.CopyFixture();
        var store = new AfterburnerProfileStore(_temp.Directory);
        var curve = SampleCurve();

        store.WriteProfile(path, 3, curve, 105_000);
        var profile = store.Read(path);

        Assert.Equal([2, 4, 5], profile.EmptySlots);
        Assert.Equal(curve.ToHex(), profile.GetCurve("Profile3")!.ToHex());
        Assert.Equal(105_000, profile.GetInt("Profile3", "CoreClkBoost"));
        Assert.Equal(["Profile1", "Profile3", "Startup", "Defaults", "Settings", "PreSuspendedMode"], profile.SectionNames);
    }

    [Fact]
    public void WriteProfile_ExistingSlotUpdatesInPlaceAndKeepsOtherSections()
    {
        string path = _temp.CopyFixture();
        string before = Text(path);
        var store = new AfterburnerProfileStore(_temp.Directory);
        var curve = SampleCurve();

        store.WriteProfile(path, 1, curve, 105_000);
        string after = Text(path);

        int profileEnd = before.IndexOf("[Startup]", StringComparison.Ordinal);
        Assert.Equal(before[profileEnd..], after[after.IndexOf("[Startup]", StringComparison.Ordinal)..]);

        var profile = store.Read(path);
        Assert.Equal(curve.ToHex(), profile.GetCurve("Profile1")!.ToHex());
        Assert.Equal(105_000, profile.GetInt("Profile1", "CoreClkBoost"));
        Assert.Equal(2_000_000, profile.GetInt("Profile1", "MemClkBoost"));
        Assert.Equal(
            before.Split("\r\n").Length,
            after.Split("\r\n").Length);
    }

    [Fact]
    public void WriteProfile_InsertsSlotsInOrder()
    {
        string path = _temp.CopyFixture();
        var store = new AfterburnerProfileStore(_temp.Directory);

        store.WriteProfile(path, 5, SampleCurve(), 1);
        store.WriteProfile(path, 2, SampleCurve(), 1);
        store.WriteProfile(path, 4, SampleCurve(), 1);

        Assert.Equal(
            ["Profile1", "Profile2", "Profile4", "Profile5", "Startup", "Defaults", "Settings", "PreSuspendedMode"],
            store.Read(path).SectionNames);
    }

    [Fact]
    public void WriteProfile_CreatesDistinctBackupsForRepeatedWrites()
    {
        string path = _temp.CopyFixture();
        var store = new AfterburnerProfileStore(_temp.Directory, new FixedTime(new DateTimeOffset(2026, 10, 25, 12, 30, 5, TimeSpan.Zero)));

        string first = store.WriteProfile(path, 2, SampleCurve(), 1);
        string second = store.WriteProfile(path, 2, SampleCurve(), 2);

        Assert.NotEqual(first, second);
        Assert.Contains(".20261025-123005.bak", first);
        Assert.True(File.Exists(first) && File.Exists(second));
    }

    [Fact]
    public void WriteProfile_PreservesLfEndingsAndMissingTrailingNewline()
    {
        string path = _temp.WriteText("lf.cfg", "; note\n[Startup]\nFormat=2\nPowerLimit=100\nFanMode=1");
        var store = new AfterburnerProfileStore(_temp.Directory);

        store.WriteProfile(path, 1, SampleCurve(), 5);
        string after = Text(path);

        Assert.DoesNotContain('\r', after);
        Assert.StartsWith("; note\n[Profile1]\nFormat=2\nPowerLimit=100\nCoreClkBoost=5\nVFCurve=", after);
        Assert.EndsWith("\n[Startup]\nFormat=2\nPowerLimit=100\nFanMode=1", after);
    }

    [Fact]
    public void WriteProfile_AppendsSectionAtEndWhenNoStartupAndFixesMissingNewline()
    {
        string path = _temp.WriteText("e.cfg", "[Settings]\r\nCaptureDefaults=0");
        var store = new AfterburnerProfileStore(_temp.Directory);

        store.WriteProfile(path, 2, SampleCurve(), 7);
        string after = Text(path);

        Assert.StartsWith("[Settings]\r\nCaptureDefaults=0\r\n[Profile2]\r\nFormat=2\r\nCoreClkBoost=7\r\nVFCurve=", after);
    }

    [Fact]
    public void WriteProfile_PreservesUtf8Bom()
    {
        byte[] content = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("[Startup]\r\nFormat=2\r\nPowerLimit=100\r\n")];
        string path = Path.Combine(_temp.Directory, "bom.cfg");
        File.WriteAllBytes(path, content);

        new AfterburnerProfileStore(_temp.Directory).WriteProfile(path, 1, SampleCurve(), 5);
        byte[] after = File.ReadAllBytes(path);

        Assert.Equal(Encoding.UTF8.GetPreamble(), after[..3]);
        Assert.Equal(0xEF, after[0]);
        Assert.NotEqual(0xEF, after[3]);
        Assert.StartsWith("[Profile1]", Encoding.UTF8.GetString(after, 3, after.Length - 3));
    }

    [Fact]
    public void WriteProfile_RejectsInvalidSlot()
    {
        string path = _temp.CopyFixture();
        var store = new AfterburnerProfileStore(_temp.Directory);

        Assert.Throws<ArgumentOutOfRangeException>(() => store.WriteProfile(path, 0, SampleCurve(), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.WriteProfile(path, 6, SampleCurve(), 1));
        Assert.Empty(Directory.GetFiles(_temp.Directory, "*.bak"));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
