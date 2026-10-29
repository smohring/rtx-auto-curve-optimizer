namespace RtxAutoCurveOptimizer.Optimization;

/// <summary>The per-user data folder (sessions, heartbeat, logs) under %LOCALAPPDATA%.</summary>
public static class AppDataFolder
{
    private static readonly string Root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string Path { get; } = System.IO.Path.Combine(Root, "RtxAutoCurveOptimizer");

    /// <summary>Folder used before the rename to RTX Auto Curve Optimizer.</summary>
    public static string LegacyPath { get; } = System.IO.Path.Combine(Root, "RtxVoltageCurveOptimizer");

    /// <summary>Copies the folder of the old name to the new one once, so earlier sessions stay available.</summary>
    public static void MigrateLegacy() => MigrateLegacy(LegacyPath, Path);

    /// <summary>
    /// Copies, never moves: tools started from a sandboxed shell get their writes redirected, and a move there
    /// once took sessions out of the real folder for good.
    /// </summary>
    public static void MigrateLegacy(string legacy, string current)
    {
        if (!Directory.Exists(legacy) || Directory.Exists(current)) return;
        foreach (var file in Directory.EnumerateFiles(legacy, "*", SearchOption.AllDirectories))
        {
            try
            {
                var target = System.IO.Path.Combine(current, System.IO.Path.GetRelativePath(legacy, file));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file in use is skipped; it stays in the legacy folder.
            }
        }
    }
}
