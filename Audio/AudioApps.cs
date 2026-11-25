using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace MusicVisualizer.Audio;

/// <summary>An app the user can choose to visualize. <see cref="Name"/> is the process name without ".exe".</summary>
public sealed record AudioApp(string Name, string DisplayName);

/// <summary>Finds apps that play audio, and the processes to capture for an app.</summary>
public static class AudioApps
{
    /// <summary>Apps with an audio session on any active output device (i.e. apps that have played sound).</summary>
    public static List<AudioApp> WithAudioSessions()
    {
        var pids = new HashSet<int>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            try
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    int pid = (int)sessions[i].GetProcessID;
                    if (pid != 0) pids.Add(pid); // 0 = Windows system sounds
                }
            }
            catch (COMException) { }
            finally { device.Dispose(); }
        }

        var apps = new Dictionary<string, AudioApp>(StringComparer.OrdinalIgnoreCase);
        foreach (int pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (!apps.ContainsKey(p.ProcessName))
                    apps[p.ProcessName] = new AudioApp(p.ProcessName, DisplayName(p));
            }
            catch (ArgumentException) { } // exited
        }
        return apps.Values.OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// The top-level processes of an app: processes with this name whose parent has a different name.
    /// Capturing one of these includes all its child processes (browsers and Spotify play audio from a child).
    /// </summary>
    public static List<int> RootProcessIds(string name)
    {
        var procs = Process.GetProcessesByName(name);
        try
        {
            var ids = procs.Select(p => p.Id).ToHashSet();
            return procs.Where(p => !ids.Contains(ParentId(p))).Select(p => p.Id).ToList();
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    private static string DisplayName(Process p)
    {
        try
        {
            string? desc = p.MainModule?.FileVersionInfo.FileDescription;
            if (!string.IsNullOrWhiteSpace(desc)) return desc.Trim();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        return p.ProcessName;
    }

    private static int ParentId(Process p)
    {
        try
        {
            var info = new PROCESS_BASIC_INFORMATION();
            if (NtQueryInformationProcess(p.Handle, 0, ref info, Marshal.SizeOf(info), out _) == 0)
                return (int)info.InheritedFromUniqueProcessId;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        return -1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass,
        ref PROCESS_BASIC_INFORMATION info, int size, out int returnLength);
}
