using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Win32;
using MusicVisualizer.Shared;

namespace MusicVisualizer.Setup
{
    public sealed class InstallOptions
    {
        public string InstallDir;
        public bool StartWithWindows;
        public bool DesktopShortcut;
    }

    /// <summary>What's already on this PC, read before showing the options page.</summary>
    public sealed class ExistingInstall
    {
        public string Dir;
        public string Version;
        public bool IsLegacyInno;
    }

    /// <summary>
    /// The actual install. Per-user only (no admin): files go to %LocalAppData%\Programs\Music Visualizer,
    /// with a Start-menu shortcut, an optional desktop shortcut and startup entry, and an
    /// "Apps" entry whose uninstall command is "MusicVisualizer.exe --uninstall".
    /// </summary>
    public static class Installer
    {
        public static string Version
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return v.Major + "." + v.Minor + "." + v.Build;
            }
        }

        public static bool HasPayload =>
            Assembly.GetExecutingAssembly().GetManifestResourceInfo("payload.exe") != null;

        public static ExistingInstall FindExisting()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(InstallInfo.UninstallKey))
            {
                if (key?.GetValue("InstallLocation") is string dir)
                    return new ExistingInstall { Dir = dir, Version = key.GetValue("DisplayVersion") as string };
            }
            using (var key = Registry.CurrentUser.OpenSubKey(InstallInfo.LegacyInnoUninstallKey))
            {
                if (key?.GetValue("InstallLocation") is string dir)
                    return new ExistingInstall { Dir = dir.TrimEnd('\\'), Version = key.GetValue("DisplayVersion") as string, IsLegacyInno = true };
            }
            return null;
        }

        /// <param name="progress">Called with (0..1, status text) from the install thread.</param>
        public static void Install(InstallOptions options, Action<double, string> progress)
        {
            string dir = Path.GetFullPath(options.InstallDir);
            string exe = Path.Combine(dir, InstallInfo.ExeName);

            progress(0.02, "Closing Music Visualizer…");
            StopRunningApp(dir);

            if (Registry.CurrentUser.OpenSubKey(InstallInfo.LegacyInnoUninstallKey) != null)
            {
                progress(0.06, "Removing the previous version…");
                RemoveLegacyInno();
            }

            progress(0.1, "Copying files…");
            Directory.CreateDirectory(dir);
            CopyPayload(exe, p => progress(0.1 + p * 0.75, "Copying files…"));

            progress(0.88, "Creating shortcuts…");
            CreateShortcut(InstallInfo.StartMenuShortcut, exe);
            if (options.DesktopShortcut) CreateShortcut(InstallInfo.DesktopShortcut, exe);
            else TryDelete(InstallInfo.DesktopShortcut);

            progress(0.94, "Registering…");
            using (var key = Registry.CurrentUser.CreateSubKey(InstallInfo.UninstallKey))
            {
                key.SetValue("DisplayName", InstallInfo.AppName);
                key.SetValue("DisplayVersion", Version);
                key.SetValue("Publisher", InstallInfo.AppName);
                key.SetValue("DisplayIcon", exe + ",0");
                key.SetValue("InstallLocation", dir);
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                key.SetValue("UninstallString", "\"" + exe + "\" " + InstallInfo.UninstallArg);
                key.SetValue("QuietUninstallString", "\"" + exe + "\" " + InstallInfo.UninstallArg + " " + InstallInfo.QuietArg);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
            }

            using (var run = Registry.CurrentUser.CreateSubKey(InstallInfo.RunKey))
            {
                if (options.StartWithWindows) run.SetValue(InstallInfo.RunValue, InstallInfo.RunCommand(exe));
                else run.DeleteValue(InstallInfo.RunValue, false);
            }

            progress(1, "Done");
        }

        /// <summary>Asks a running copy to quit (so it restores the desktop), then force-closes stragglers.</summary>
        private static void StopRunningApp(string dir)
        {
            EventWaitHandle quit;
            if (EventWaitHandle.TryOpenExisting(InstallInfo.QuitEvent, out quit))
            {
                using (quit) quit.Set();
                for (int i = 0; i < 50; i++)
                {
                    Mutex m;
                    if (!Mutex.TryOpenExisting(InstallInfo.InstanceMutex, out m)) break;
                    m.Dispose();
                    Thread.Sleep(100);
                }
            }

            string target = Path.Combine(dir, InstallInfo.ExeName);
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(InstallInfo.ExeName)))
            {
                try
                {
                    if (string.Equals(p.MainModule.FileName, target, StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill();
                        p.WaitForExit(5000);
                    }
                }
                catch (Exception) { } // not ours to touch, or already gone
                finally { p.Dispose(); }
            }
        }

        /// <summary>Version 1.0.0 used Inno Setup. Run its uninstaller silently so it doesn't leave a second entry behind.</summary>
        private static void RemoveLegacyInno()
        {
            string uninstaller;
            using (var key = Registry.CurrentUser.OpenSubKey(InstallInfo.LegacyInnoUninstallKey))
                uninstaller = (key?.GetValue("UninstallString") as string)?.Trim('"');
            if (uninstaller == null || !File.Exists(uninstaller)) return;

            using (var p = Process.Start(new ProcessStartInfo(uninstaller, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART")
                   { UseShellExecute = false }))
            {
                p.WaitForExit(60000);
            }
            // Inno's uninstaller hands off to a temp copy; give it a moment to finish deleting.
            for (int i = 0; i < 40 && Registry.CurrentUser.OpenSubKey(InstallInfo.LegacyInnoUninstallKey) != null; i++)
                Thread.Sleep(250);
        }

        private static void CopyPayload(string exe, Action<double> progress)
        {
            string temp = exe + ".new";
            using (var src = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.exe"))
            using (var dst = File.Create(temp))
            {
                var buffer = new byte[1 << 20];
                long total = src.Length, done = 0;
                int read;
                while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    dst.Write(buffer, 0, read);
                    done += read;
                    progress((double)done / total);
                }
            }

            // Replace the old exe. It can stay locked for a moment after the app exits.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(exe)) File.Delete(exe);
                    File.Move(temp, exe);
                    return;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 20)
                {
                    Thread.Sleep(250);
                }
            }
        }

        private static void CreateShortcut(string path, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            try
            {
                dynamic link = shell.CreateShortcut(path);
                link.TargetPath = target;
                link.WorkingDirectory = Path.GetDirectoryName(target);
                link.IconLocation = target + ",0";
                link.Description = "Music visualizer and live wallpaper";
                link.Save();
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }

        private static void TryDelete(string file)
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>Starts the installed app. With no arguments it opens settings (the welcome screen on first run).</summary>
        public static void Launch(string dir)
        {
            Process.Start(new ProcessStartInfo(Path.Combine(dir, InstallInfo.ExeName)) { UseShellExecute = true, WorkingDirectory = dir });
        }
    }
}
