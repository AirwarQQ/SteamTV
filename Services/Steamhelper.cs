// Controller detection (WMI), Big Picture status, minimizing/launching Steam.
using System;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;

namespace SteamTV
{

    internal static class SteamHelper
    {
        private static readonly Regex SwitchProRe = new Regex(@"VID_057E.*PID_2009", RegexOptions.IgnoreCase);
        private static readonly Regex SwitchProInstRe = new Regex(@"VID&0002057E.*PID&2009", RegexOptions.IgnoreCase);
        private static readonly Regex EightBitDoRe = new Regex(@"VID_37D7.*PID_2501", RegexOptions.IgnoreCase);
        private const string GamepadUsage = "UP:0001_U:0005";

        private static ManagementObjectSearcher _searcher;

        private static ManagementObjectSearcher GetSearcher()
        {
            if (_searcher == null)
            {
                // HIDClass
                _searcher = new ManagementObjectSearcher(
                    "root\\CIMV2",
                    "SELECT PNPDeviceID, HardwareID, ConfigManagerErrorCode FROM Win32_PnPEntity " +
                    "WHERE ClassGuid='{745a17a0-74d3-11d0-b6fe-00a0c90f57da}'");
            }
            return _searcher;
        }

        public static bool IsControllerConnected()
        {
            try
            {
                using (var results = GetSearcher().Get())
                {
                    foreach (ManagementObject mo in results)
                    using (mo)
                    {
                        string[] hwids = mo["HardwareID"] as string[] ?? Array.Empty<string>();
                        string instanceId = mo["PNPDeviceID"] as string ?? "";
                        uint? cmErr = null;
                        try { if (mo["ConfigManagerErrorCode"] != null) cmErr = Convert.ToUInt32(mo["ConfigManagerErrorCode"]); }
                        catch { }

                        bool usage = hwids.Any(h => h.IndexOf(GamepadUsage, StringComparison.OrdinalIgnoreCase) >= 0);
                        if (!usage) continue;

                        // Switch Pro: VID/PID match + OK status (ConfigManagerErrorCode == 0)
                        bool switchVidPid = hwids.Any(h => SwitchProRe.IsMatch(h)) || SwitchProInstRe.IsMatch(instanceId);
                        if (switchVidPid && cmErr.HasValue && cmErr.Value == 0)
                            return true;

                        // 8BitDo / 37D7:2501: device presence is sufficient
                        if (hwids.Any(h => EightBitDoRe.IsMatch(h)))
                            return true;
                    }
                }
            }
            catch { return false; }
            return false;
        }

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

        public static void StartBigPicture(string steamPath)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = steamPath,
                    Arguments = "-start steam://open/bigpicture",
                    UseShellExecute = true
                });
            }
            catch { /* steam not found — ignored, caller can log if needed */ }
        }
    }

}