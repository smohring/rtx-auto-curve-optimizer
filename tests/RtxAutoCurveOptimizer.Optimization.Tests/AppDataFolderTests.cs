namespace RtxAutoCurveOptimizer.Optimization.Tests;

public sealed class AppDataFolderTests
{
    [Fact]
    public void LegacyFolder_IsCopiedOnceNeverDeletedAndNeverOverwritesTheNewOne()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var legacy = Path.Combine(root.FullName, "RtxVoltageCurveOptimizer");
            var current = Path.Combine(root.FullName, "RtxAutoCurveOptimizer");
            Directory.CreateDirectory(Path.Combine(legacy, "sessions"));
            File.WriteAllText(Path.Combine(legacy, "sessions", "a.json"), "{}");

            AppDataFolder.MigrateLegacy(legacy, current);
            Assert.True(File.Exists(Path.Combine(legacy, "sessions", "a.json")), "the legacy data must never be deleted");
            Assert.True(File.Exists(Path.Combine(current, "sessions", "a.json")));

            File.WriteAllText(Path.Combine(legacy, "b.json"), "{}");
            AppDataFolder.MigrateLegacy(legacy, current);
            Assert.True(File.Exists(Path.Combine(legacy, "b.json")));
            Assert.False(File.Exists(Path.Combine(current, "b.json")));
        }
        finally
        {
            root.Delete(true);
        }
    }
}
