using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace MusicVisualizer.Core;

/// <summary>Gives memory back to Windows while the app has nothing to show.</summary>
public static class Memory
{
    /// <summary>
    /// Compacts the .NET heap and asks Windows to page out what the process isn't using right now
    /// (the same as Task Manager's "working set" shrinking when an app is idle). Pages come back on
    /// demand when drawing resumes. Called when the visualizer stops, hides or is covered.
    /// </summary>
    public static void Trim()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        using var self = Process.GetCurrentProcess();
        SetProcessWorkingSetSize(self.Handle, -1, -1);
    }

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint minimum, nint maximum);
}
