using System.Diagnostics;

namespace RtxAutoCurveOptimizer.Afterburner;

/// <summary>Detects a running MSI Afterburner, which rewrites its profiles on exit.</summary>
public static class AfterburnerProcess
{
    /// <summary>True if <c>MSIAfterburner.exe</c> is running.</summary>
    public static bool IsRunning()
    {
        var processes = Process.GetProcessesByName("MSIAfterburner");
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var p in processes)
                p.Dispose();
        }
    }
}
