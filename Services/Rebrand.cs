// The app used to be called SteamTV. On the first start after an update, this carries everything
// over to the NekoCouch names, so nothing has to be set up again:
//   • settings: HKCU\Software\SteamTV → HKCU\Software\NekoCouch (the old key stays, for a downgrade);
//   • the exe: SteamTV.exe → NekoCouch.exe in the same folder (the old updater put the new build
//     there under the old name); shortcuts on the desktop, in the Start menu and pinned to the
//     taskbar that pointed at it follow;
//   • autostart: the "SteamTV_Controller" Run value → "NekoCouch_Controller", at the new path;
//   • %LOCALAPPDATA%\SteamTV (bundled adb.exe, log) → %LOCALAPPDATA%\NekoCouch.
// Each step is best effort: whatever can't be moved keeps working where it is.
using System;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace NekoCouch
{
    internal static class Rebrand
    {
        private const string OldName = "SteamTV", NewName = "NekoCouch";
        public const string OldRunValueName = "SteamTV_Controller";

        // Where the running exe is now. Assembly.Location keeps reporting the path it was loaded
        // from, which is gone once the exe has been renamed.
        public static string ExePath { get; private set; } = Assembly.GetEntryAssembly().Location;

        public static void Run(string runKeyPath, string runValueName)
        {
            Try(MigrateSettings);
            Try(MigrateDataFolder);
            string oldExe = ExePath;
            Try(RenameExe);
            Try(() => MigrateAutostart(runKeyPath, runValueName));
            if (!string.Equals(oldExe, ExePath, StringComparison.OrdinalIgnoreCase))
                Try(() => RetargetShortcuts(oldExe, ExePath));
            // The old updater's leftover, which the new name's clean-up no longer looks for.
            Try(() => File.Delete(Path.Combine(Path.GetDirectoryName(ExePath), OldName + ".old.exe")));
        }

        private static void Try(Action step) { try { step(); } catch { } }

        private static void MigrateSettings()
        {
            using (var neu = Registry.CurrentUser.OpenSubKey(@"Software\" + NewName))
                if (neu != null) return;
            using (var old = Registry.CurrentUser.OpenSubKey(@"Software\" + OldName))
            {
                if (old == null) return;
                using (var neu = Registry.CurrentUser.CreateSubKey(@"Software\" + NewName))
                    CopyKey(old, neu);
            }
        }

        private static void CopyKey(RegistryKey from, RegistryKey to)
        {
            foreach (string name in from.GetValueNames())
                to.SetValue(name, from.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames), from.GetValueKind(name));
            foreach (string sub in from.GetSubKeyNames())
                using (var f = from.OpenSubKey(sub))
                using (var t = to.CreateSubKey(sub))
                    CopyKey(f, t);
        }

        private static void MigrateDataFolder()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string oldDir = Path.Combine(local, OldName), newDir = Path.Combine(local, NewName);
            if (!Directory.Exists(oldDir) || Directory.Exists(newDir)) return;
            try { Directory.Move(oldDir, newDir); }
            catch (IOException)
            {
                // The old adb.exe still runs as the adb server and holds the folder — stop it, try once more.
                string adb = Path.Combine(oldDir, "adb.exe");
                if (File.Exists(adb))
                    using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(adb, "kill-server") { UseShellExecute = false, CreateNoWindow = true }))
                        p?.WaitForExit(5000);
                Directory.Move(oldDir, newDir);
            }
            foreach (var (from, to) in new[] { ("steamtv.log", "nekocouch.log"), ("steamtv.old.log", "nekocouch.old.log") })
                Try(() => { string f = Path.Combine(newDir, from), t = Path.Combine(newDir, to); if (File.Exists(f) && !File.Exists(t)) File.Move(f, t); });
        }

        private static void RenameExe()
        {
            if (!string.Equals(Path.GetFileName(ExePath), OldName + ".exe", StringComparison.OrdinalIgnoreCase)) return;
            string target = Path.Combine(Path.GetDirectoryName(ExePath), NewName + ".exe");
            if (File.Exists(target)) return;
            File.Move(ExePath, target);   // allowed while it runs, same as the updater's swap
            ExePath = target;
        }

        private static void MigrateAutostart(string runKeyPath, string runValueName)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(runKeyPath, true))
            {
                if (k == null || k.GetValue(OldRunValueName) == null) return;
                k.SetValue(runValueName, "\"" + ExePath + "\" -autostart");
                k.DeleteValue(OldRunValueName, false);
            }
        }

        private static void RetargetShortcuts(string oldExe, string newExe)
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            object shell = Activator.CreateInstance(shellType);
            foreach (string dir in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                Path.Combine(appData, @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"),
                Path.Combine(appData, @"Microsoft\Internet Explorer\Quick Launch\User Pinned\StartMenu"),
            })
            {
                if (!Directory.Exists(dir)) continue;
                bool pinned = dir.IndexOf("User Pinned", StringComparison.OrdinalIgnoreCase) >= 0;
                foreach (string lnk in Directory.GetFiles(dir, "*.lnk", pinned ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories))
                    Try(() =>
                    {
                        object sc = Call(shell, "CreateShortcut", lnk);
                        string target = (string)Get(sc, "TargetPath");
                        if (!string.Equals(target, oldExe, StringComparison.OrdinalIgnoreCase)) return;
                        Set(sc, "TargetPath", newExe);
                        Set(sc, "WorkingDirectory", Path.GetDirectoryName(newExe));
                        Set(sc, "IconLocation", newExe + ",0");
                        Call(sc, "Save");
                        // A pinned shortcut is matched by its file; elsewhere the name is what people see.
                        if (!pinned && string.Equals(Path.GetFileNameWithoutExtension(lnk), OldName, StringComparison.OrdinalIgnoreCase))
                        {
                            string renamed = Path.Combine(Path.GetDirectoryName(lnk), NewName + ".lnk");
                            if (!File.Exists(renamed)) File.Move(lnk, renamed);
                        }
                    });
            }
        }

        private static object Call(object o, string name, params object[] args) =>
            o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, args);
        private static object Get(object o, string name) =>
            o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, null);
        private static void Set(object o, string name, object value) =>
            o.GetType().InvokeMember(name, BindingFlags.SetProperty, null, o, new[] { value });
    }
}
