using System.Globalization;

namespace RtxAutoCurveOptimizer.Afterburner;

/// <summary>Read-only view of one per-GPU Afterburner profile file.</summary>
public sealed class AfterburnerProfile
{
    /// <summary>Number of user profile slots (<c>[Profile1]</c> to <c>[Profile5]</c>).</summary>
    public const int SlotCount = 5;

    private readonly IniDocument _doc;

    internal AfterburnerProfile(string path, IniDocument doc)
    {
        Path = path;
        _doc = doc;
    }

    /// <summary>Full path of the profile file.</summary>
    public string Path { get; }

    /// <summary>Section names in file order.</summary>
    public IReadOnlyList<string> SectionNames => _doc.Sections().Select(s => s.Name).ToList();

    /// <summary>Returns the trimmed value of a key, or null if section or key is missing.</summary>
    public string? GetValue(string section, string key) => _doc.GetValue(section, key);

    /// <summary>Returns a key's integer value, or null if missing or not a number.</summary>
    public int? GetInt(string section, string key) =>
        int.TryParse(GetValue(section, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;

    /// <summary>Decodes the <c>VFCurve</c> of a section ([Defaults], [Startup], [ProfileN]); null if absent or empty.</summary>
    /// <exception cref="FormatException">The key exists but is not a valid blob.</exception>
    public AfterburnerVfCurve? GetCurve(string section)
    {
        string? hex = GetValue(section, "VFCurve");
        return string.IsNullOrEmpty(hex) ? null : AfterburnerVfCurve.Parse(hex);
    }

    /// <summary>True if the slot (1-5) has no section or an empty <c>VFCurve</c>.</summary>
    public bool IsSlotEmpty(int slot) =>
        string.IsNullOrEmpty(GetValue(SlotSection(slot), "VFCurve"));

    /// <summary>Slot numbers (1-5) that hold no curve.</summary>
    public IReadOnlyList<int> EmptySlots =>
        Enumerable.Range(1, SlotCount).Where(IsSlotEmpty).ToList();

    /// <summary>Section name of a slot, e.g. <c>Profile3</c>.</summary>
    public static string SlotSection(int slot)
    {
        if (slot is < 1 or > SlotCount)
            throw new ArgumentOutOfRangeException(nameof(slot), $"Slot must be 1 to {SlotCount}.");
        return "Profile" + slot.ToString(CultureInfo.InvariantCulture);
    }
}
