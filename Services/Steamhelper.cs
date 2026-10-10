// Big Picture status, minimizing/launching Steam. (Device detection lives in DeviceScanner.)
using System;
using System.Diagnostics;
using System.IO;

namespace SteamTV
{

    internal static class SteamHelper
    {
        public static bool IsBigPictureRunning()
        {
            foreach (var p in Process.GetProcessesByName("steamwebhelper"))
            {
                try
                {
                    if (!string.IsNullOrEmpty(p.MainWindowTitle) &&
                        p.MainWindowTitle.IndexOf("Big Picture", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
                catch { }
                finally { p.Dispose(); }
            }
            return false;
        }

        // Leaves Big Picture: Steam's own URL first, then WM_CLOSE on the Big Picture window as a
        // fallback (closing that window exits BP mode). Returns true once BP is confirmed gone.
        public static bool CloseBigPicture(System.Threading.CancellationToken ct, out string how)
        {
            if (!IsBigPictureRunning()) { how = "not running"; return true; }

            try { Process.Start(new ProcessStartInfo { FileName = "steam://close/bigpicture", UseShellExecute = true }); }
            catch { }
            if (WaitUntilBigPictureGone(ct, 4000)) { how = "steam://close/bigpicture"; return true; }

            foreach (var p in Process.GetProcessesByName("steamwebhelper"))
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero &&
                        p.MainWindowTitle.IndexOf("Big Picture", StringComparison.OrdinalIgnoreCase) >= 0)
                        Native.PostMessage(p.MainWindowHandle, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }
                catch { }
                finally { p.Dispose(); }
            }
            if (WaitUntilBigPictureGone(ct, 4000)) { how = "closed its window"; return true; }

            how = "still open after steam://close/bigpicture and WM_CLOSE";
            return false;
        }

        private static bool WaitUntilBigPictureGone(System.Threading.CancellationToken ct, int timeoutMs)
        {
            for (int waited = 0; waited < timeoutMs; waited += 250)
            {
                if (!IsBigPictureRunning()) return true;
                if (ct.WaitHandle.WaitOne(250)) return false;
            }
            return !IsBigPictureRunning();
        }

        public static void MinimizeSteamWindow()
        {
            foreach (var p in Process.GetProcessesByName("steamwebhelper"))
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero &&
                        string.Equals(p.MainWindowTitle, "Steam", StringComparison.Ordinal))
                        Native.ShowWindowAsync(p.MainWindowHandle, Native.SW_MINIMIZE);
                }
                catch { }
                finally { p.Dispose(); }
            }
        }

        public static bool StartBigPicture(string steamPath, out string error)
        {
            if (string.IsNullOrWhiteSpace(steamPath) || !File.Exists(steamPath))
            {
                error = "steam.exe not found at '" + steamPath + "' — check the path on the Advanced tab.";
                return false;
            }
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = steamPath,
                    Arguments = "-start steam://open/bigpicture",
                    UseShellExecute = true
                });
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }

}
