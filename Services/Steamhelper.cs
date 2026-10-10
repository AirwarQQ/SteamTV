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
