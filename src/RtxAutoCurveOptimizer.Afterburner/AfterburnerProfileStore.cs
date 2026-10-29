using System.Globalization;
using RtxAutoCurveOptimizer.Core;

namespace RtxAutoCurveOptimizer.Afterburner;

/// <summary>Finds, reads and writes the per-GPU Afterburner profile files in one profiles directory.</summary>
public sealed class AfterburnerProfileStore
{
    private static readonly string[] CopiedFromStartup =
        ["PowerLimit", "MemClkBoost", "FanMode", "FanSpeed", "FanMode2", "FanSpeed2", "CoreVoltageBoost"];

    private readonly TimeProvider _time;

    /// <param name="profilesDirectory">Folder holding the <c>*.cfg</c> files.</param>
    /// <param name="timeProvider">Clock for backup names; defaults to the system clock.</param>
    public AfterburnerProfileStore(string profilesDirectory, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(profilesDirectory);
        ProfilesDirectory = profilesDirectory;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Folder holding the profile files.</summary>
    public string ProfilesDirectory { get; }

    /// <summary>Creates a store for the installed Afterburner's <c>Profiles</c> folder.</summary>
    public static AfterburnerProfileStore CreateDefault() => new(AfterburnerInstall.FindProfilesDirectory());

    /// <summary>
    /// Finds the profile file whose name starts with <c>VEN_10DE&amp;DEV_xxxx&amp;SUBSYS_xxxxxxxx&amp;</c>
    /// for the GPU, or null. Accepts the device id as plain 16 bit (0x2C02) or NVAPI style with the
    /// device in the high half (0x2C0210DE). Several matches are narrowed by the bus number.
    /// </summary>
    public string? FindProfileFile(GpuIdentity gpu)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        if (!Directory.Exists(ProfilesDirectory))
            return null;

        uint device = gpu.DeviceId > 0xFFFF ? gpu.DeviceId >> 16 : gpu.DeviceId;
        string prefix = $"VEN_10DE&DEV_{device:X4}&SUBSYS_{gpu.SubsystemId:X8}&";
        var matches = Directory.EnumerateFiles(ProfilesDirectory, "*.cfg")
            .Where(f => Path.GetFileName(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (matches.Count <= 1)
            return matches.Count == 1 ? matches[0] : null;

        string bus = $"&BUS_{gpu.BusNumber}&";
        return matches.FirstOrDefault(f => Path.GetFileName(f).Contains(bus, StringComparison.OrdinalIgnoreCase))
            ?? matches[0];
    }

    /// <summary>Reads a profile file.</summary>
    public AfterburnerProfile Read(string path) =>
        new(path, IniDocument.Parse(File.ReadAllBytes(path)));

    /// <summary>
    /// Writes <paramref name="curve"/> into <c>[Profile{slot}]</c> after copying the file to a timestamped
    /// <c>.bak</c>. Power, memory, fan and voltage keys come from <c>[Startup]</c>; all other content
    /// and the line endings stay as they are. Returns the backup path.
    /// </summary>
    /// <param name="path">Profile file to modify.</param>
    /// <param name="slot">Profile slot, 1 to 5.</param>
    /// <param name="curve">Blob to store as <c>VFCurve</c>.</param>
    /// <param name="coreClkBoostKhz">Offset of the cap point in kHz, stored as <c>CoreClkBoost</c>.</param>
    public string WriteProfile(string path, int slot, AfterburnerVfCurve curve, int coreClkBoostKhz)
    {
        ArgumentNullException.ThrowIfNull(curve);
        string section = AfterburnerProfile.SlotSection(slot);

        byte[] original = File.ReadAllBytes(path);
        var doc = IniDocument.Parse(original);

        var entries = new List<KeyValuePair<string, string>> { new("Format", "2") };
        AddIfPresent(entries, doc, "PowerLimit");
        entries.Add(new("CoreClkBoost", coreClkBoostKhz.ToString(CultureInfo.InvariantCulture)));
        entries.Add(new("VFCurve", curve.ToHex()));
        foreach (string key in CopiedFromStartup.Skip(1))
            AddIfPresent(entries, doc, key);

        if (doc.FindSection(section) is null)
            doc.InsertSection(InsertionLine(doc, slot), section, entries);
        else
            foreach (var (key, value) in entries)
                doc.SetValue(section, key, value);

        string backup = CreateBackup(path);
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, doc.ToBytes());
        File.Move(temp, path, overwrite: true);
        return backup;
    }

    private static void AddIfPresent(List<KeyValuePair<string, string>> entries, IniDocument doc, string key)
    {
        if (doc.GetValue("Startup", key) is { } value)
            entries.Add(new(key, value));
    }

    // New slot sections go after the last lower slot, else before the next higher slot, else before [Startup].
    private static int InsertionLine(IniDocument doc, int slot)
    {
        var sections = doc.Sections();
        var lower = sections.Where(s => SlotOf(s.Name) is { } n && n < slot).ToList();
        if (lower.Count > 0)
            return lower.Max(s => s.End);
        var higher = sections.Where(s => SlotOf(s.Name) is { } n && n > slot).ToList();
        if (higher.Count > 0)
            return higher.Min(s => s.Start);
        foreach (var s in sections)
        {
            if (string.Equals(s.Name, "Startup", StringComparison.OrdinalIgnoreCase))
                return s.Start;
        }
        return doc.LineCount;
    }

    private static int? SlotOf(string name) =>
        name.StartsWith("Profile", StringComparison.OrdinalIgnoreCase)
        && int.TryParse(name.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out int n)
        && n is >= 1 and <= AfterburnerProfile.SlotCount
            ? n
            : null;

    private string CreateBackup(string path)
    {
        string stamp = _time.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        for (int attempt = 0; ; attempt++)
        {
            string suffix = attempt == 0 ? "" : "-" + attempt.ToString(CultureInfo.InvariantCulture);
            string backup = $"{path}.{stamp}{suffix}.bak";
            if (File.Exists(backup))
                continue;
            File.Copy(path, backup);
            return backup;
        }
    }
}
