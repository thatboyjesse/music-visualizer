using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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
    /// "Apps" entry whose uninstall command is "MusicVisualizer.exe --uninstall". The payload holds the
    /// engine (MusicVisualizer.exe, Rust) and the settings app (MusicVisualizerSettings.exe and the .NET runtime).
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
            Assembly.GetExecutingAssembly().GetManifestResourceInfo("payload.zip") != null;

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
            long size = ExtractPayload(dir, p => progress(0.1 + p * 0.75, "Copying files…"));

            progress(0.88, "Creating shortcuts…");
            // "Music Visualizer" runs the wallpaper (the main way to use it); "... Settings" opens settings.
            CreateShortcut(InstallInfo.StartMenuShortcut, exe, "", "Run the music visualizer as your desktop wallpaper");
            CreateShortcut(InstallInfo.SettingsShortcut, exe, InstallInfo.SettingsArg, "Change how Music Visualizer looks and what it listens to");
            if (options.DesktopShortcut) CreateShortcut(InstallInfo.DesktopShortcut, exe, "", "Run the music visualizer as your desktop wallpaper");
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
                key.SetValue("EstimatedSize", (int)(size / 1024), RegistryValueKind.DWord);
            }

            using (var run = Registry.CurrentUser.CreateSubKey(InstallInfo.RunKey))
            {
                if (options.StartWithWindows) run.SetValue(InstallInfo.RunValue, InstallInfo.RunCommand(exe));
                else run.DeleteValue(InstallInfo.RunValue, false);
            }

            progress(1, "Done");
        }

        /// <summary>
        /// Removes Music Visualizer: the app, shortcuts, startup and Apps entries, and settings.
        /// Done by Setup itself (not by running the app's --uninstall) so it also cleans up a
        /// broken install whose exe is missing. The 1.0.0 Inno Setup install runs its own uninstaller.
        /// </summary>
        public static void Uninstall(ExistingInstall existing, Action<double, string> progress)
        {
            string dir = Path.GetFullPath(existing.Dir);

            progress(0.1, "Closing Music Visualizer…");
            StopRunningApp(dir);

            if (existing.IsLegacyInno)
            {
                progress(0.4, "Removing…");
                RemoveLegacyInno();
                progress(1, "Done");
                return;
            }

            progress(0.4, "Removing shortcuts and settings…");
            using (var run = Registry.CurrentUser.OpenSubKey(InstallInfo.RunKey, true))
                run?.DeleteValue(InstallInfo.RunValue, false);
            Registry.CurrentUser.DeleteSubKeyTree(InstallInfo.UninstallKey, false);
            TryDelete(InstallInfo.StartMenuShortcut);
            TryDelete(InstallInfo.SettingsShortcut);
            TryDelete(InstallInfo.DesktopShortcut);
            DeleteFolder(InstallInfo.DataDir);

            progress(0.7, "Removing files…");
            RemoveInstalledFiles(dir);
            progress(1, "Done");
        }

        /// <summary>
        /// Deletes exactly the files Setup installed (from the file list), then any folders that are
        /// left empty, including the install folder itself. Anything else in the folder is left alone.
        /// Installs from before 1.4.0 have no list; they were a single MusicVisualizer.exe.
        /// </summary>
        private static void RemoveInstalledFiles(string dir)
        {
            var files = InstallInfo.ReadManifest(dir);
            if (files.Count == 0) files.Add(InstallInfo.ExeName);
            files.Add(InstallInfo.ManifestName);
            foreach (var f in files) DeleteFile(Path.Combine(dir, f));
            DeleteEmptyFolders(dir);
        }

        private static void DeleteEmptyFolders(string dir)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var sub in Directory.GetDirectories(dir)) DeleteEmptyFolders(sub);
            try { if (Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>Deletes a file, retrying briefly while it's still locked (an app that just exited, antivirus).</summary>
        private static void DeleteFile(string path)
        {
            for (int i = 0; i < 20 && File.Exists(path); i++)
            {
                try { File.Delete(path); }
                catch (IOException) { Thread.Sleep(250); }
                catch (UnauthorizedAccessException) { Thread.Sleep(250); }
            }
        }

        /// <summary>Deletes a folder, retrying briefly while files are still locked (an app that just exited, antivirus).</summary>
        private static void DeleteFolder(string dir)
        {
            for (int i = 0; i < 20 && Directory.Exists(dir); i++)
            {
                try { Directory.Delete(dir, true); }
                catch (IOException) { Thread.Sleep(250); }
                catch (UnauthorizedAccessException) { Thread.Sleep(250); }
            }
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

            // The settings window (2.0+), if open.
            EventWaitHandle helperQuit;
            if (EventWaitHandle.TryOpenExisting(InstallInfo.HelperQuitEvent, out helperQuit))
            {
                using (helperQuit) helperQuit.Set();
                for (int i = 0; i < 30; i++)
                {
                    Mutex m;
                    if (!Mutex.TryOpenExisting(InstallInfo.HelperMutex, out m)) break;
                    m.Dispose();
                    Thread.Sleep(100);
                }
            }

            foreach (string exeName in new[] { InstallInfo.ExeName, InstallInfo.SettingsExeName })
                KillCopies(Path.Combine(dir, exeName));
        }

        /// <summary>Force-closes processes running this exact file (a copy that didn't respond to Quit).</summary>
        private static void KillCopies(string target)
        {
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(target)))
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

        /// <summary>
        /// Unzips the app folder into the install folder, overwriting the previous version, removes files
        /// the previous version had that this one doesn't, and writes the new file list. Returns the total size.
        /// </summary>
        private static long ExtractPayload(string dir, Action<double> progress)
        {
            var oldFiles = InstallInfo.ReadManifest(dir);
            var newFiles = new List<string>();
            long size = 0;

            using (var src = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
            using (var zip = new ZipArchive(src, ZipArchiveMode.Read))
            {
                int done = 0;
                foreach (var entry in zip.Entries)
                {
                    done++;
                    if (string.IsNullOrEmpty(entry.Name)) continue; // a folder entry
                    string relative = entry.FullName.Replace('/', '\\');
                    string target = Path.GetFullPath(Path.Combine(dir, relative));
                    if (!target.StartsWith(Path.GetFullPath(dir) + "\\", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Bad path in the installer data: " + entry.FullName);

                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    for (int attempt = 0; ; attempt++)
                    {
                        try
                        {
                            using (var input = entry.Open())
                            using (var output = File.Create(target))
                                input.CopyTo(output);
                            break;
                        }
                        // The previous version's files can stay locked for a moment after it exits.
                        catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 20)
                        {
                            Thread.Sleep(250);
                        }
                    }
                    newFiles.Add(relative);
                    size += entry.Length;
                    progress((double)done / zip.Entries.Count);
                }
            }

            var keep = new HashSet<string>(newFiles, StringComparer.OrdinalIgnoreCase);
            foreach (var f in oldFiles)
                if (!keep.Contains(f)) DeleteFile(Path.Combine(dir, f));

            File.WriteAllLines(Path.Combine(dir, InstallInfo.ManifestName), newFiles);
            return size;
        }

        private static void CreateShortcut(string path, string target, string arguments, string description)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            try
            {
                dynamic link = shell.CreateShortcut(path);
                link.TargetPath = target;
                link.Arguments = arguments;
                link.WorkingDirectory = Path.GetDirectoryName(target);
                link.IconLocation = target + ",0";
                link.Description = description;
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

        /// <summary>Starts the installed app. With no arguments it runs as the wallpaper, with no window.</summary>
        public static void Launch(string dir)
        {
            Process.Start(new ProcessStartInfo(Path.Combine(dir, InstallInfo.ExeName)) { UseShellExecute = true, WorkingDirectory = dir });
        }
    }
}
