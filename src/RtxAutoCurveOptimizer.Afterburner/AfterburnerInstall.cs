using Microsoft.Win32;

namespace RtxAutoCurveOptimizer.Afterburner;

/// <summary>Locates the MSI Afterburner installation.</summary>
public static class AfterburnerInstall
{
    /// <summary>Install directory used when the registry has no usable entry.</summary>
    public const string DefaultDirectory = @"C:\Program Files (x86)\MSI Afterburner";

    private const string RegistrySubKey = @"SOFTWARE\WOW6432Node\MSI\Afterburner";

    /// <summary>
    /// Returns the install directory from the registry (<c>InstallDir</c>, else <c>InstallPath</c>,
    /// which may point at the exe) if it exists, otherwise <see cref="DefaultDirectory"/>.
    /// <paramref name="readRegistryValue"/> replaces the registry lookup (value name to value).
    /// </summary>
    public static string FindInstallDirectory(Func<string, string?>? readRegistryValue = null)
    {
        readRegistryValue ??= ReadRegistryValue;
        foreach (string name in new[] { "InstallDir", "InstallPath" })
        {
            string? value = readRegistryValue(name)?.Trim().Trim('"');
            if (string.IsNullOrEmpty(value))
                continue;
            string dir = value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(value) ?? value
                : value;
            if (Directory.Exists(dir))
                return dir;
        }
        return DefaultDirectory;
    }

    /// <summary>The <c>Profiles</c> folder inside the install directory.</summary>
    public static string FindProfilesDirectory(Func<string, string?>? readRegistryValue = null) =>
        Path.Combine(FindInstallDirectory(readRegistryValue), "Profiles");

    private static string? ReadRegistryValue(string name)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(RegistrySubKey);
            return key?.GetValue(name) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
