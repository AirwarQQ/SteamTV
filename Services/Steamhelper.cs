// Controller detection (WMI), Big Picture status, minimizing/launching Steam.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;

namespace SteamTV
{

    // Represents a gamepad currently connected and visible to Windows.
    internal sealed class ConnectedGamepad
    {
        public string HardwareId;    // canonical "VID_XXXX&PID_YYYY"
        public string FriendlyName;  // from WMI Name field

        public override string ToString() =>
            string.IsNullOrEmpty(FriendlyName) ? HardwareId
                : FriendlyName + "  (" + HardwareId + ")";
    }

    internal static class SteamHelper
    {
        private const string GamepadUsage = "UP:0001_U:0005";  // HID usage: Generic Desktop / Game Pad

        // Cached searcher for the 2-second polling loop — created once, .Get() runs the query each time.
        // Call Shutdown() when the app exits to release the underlying WMI handle.
        private static ManagementObjectSearcher _searcher;

        private static ManagementObjectSearcher GetSearcher()
        {
            if (_searcher == null)
            {
                _searcher = new ManagementObjectSearcher(
                    "root\\CIMV2",
                    "SELECT PNPDeviceID, HardwareID, ConfigManagerErrorCode FROM Win32_PnPEntity " +
                    "WHERE ClassGuid='{745a17a0-74d3-11d0-b6fe-00a0c90f57da}'",
                    new EnumerationOptions { Timeout = TimeSpan.FromSeconds(5) });
            }
            return _searcher;
        }

        public static void Shutdown()
        {
            _searcher?.Dispose();
            _searcher = null;
        }

        // Normalize any HID HardwareID string to "VID_XXXX&PID_YYYY" (upper-case, no revision suffix).
        private static string ExtractVidPid(string hwid)
        {
            if (string.IsNullOrEmpty(hwid)) return null;
            var v = Regex.Match(hwid, @"VID_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase);
            var p = Regex.Match(hwid, @"PID_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase);
            if (!v.Success || !p.Success) return null;
            return "VID_" + v.Groups[1].Value.ToUpperInvariant()
                 + "&PID_" + p.Groups[1].Value.ToUpperInvariant();
        }

        // Enumerate all HID game controllers visible to Windows right now.
        // Uses a fresh searcher so callers get an accurate snapshot (not cached).
        // Called on user demand (Refresh button), not in the hot 2-second polling loop.
        public static List<ConnectedGamepad> EnumerateGamepads(Action<string> log = null)
        {
            var result = new List<ConnectedGamepad>();
            try
            {
                using (var s = new ManagementObjectSearcher(
                    "root\\CIMV2",
                    "SELECT Name, PNPDeviceID, HardwareID FROM Win32_PnPEntity " +
                    "WHERE ClassGuid='{745a17a0-74d3-11d0-b6fe-00a0c90f57da}'",
                    new EnumerationOptions { Timeout = TimeSpan.FromSeconds(10) }))
                using (var items = s.Get())
                {
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (ManagementObject mo in items)
                    using (mo)
                    {
                        string[] hwids = mo["HardwareID"] as string[] ?? Array.Empty<string>();
                        if (!hwids.Any(h => h.IndexOf(GamepadUsage, StringComparison.OrdinalIgnoreCase) >= 0))
                            continue;

                        string vidPid = hwids.Select(ExtractVidPid).FirstOrDefault(x => x != null);
                        if (vidPid == null || !seen.Add(vidPid)) continue;

                        result.Add(new ConnectedGamepad
                        {
                            HardwareId = vidPid,
                            FriendlyName = mo["Name"] as string ?? vidPid
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[SteamHelper] EnumerateGamepads: " + ex.Message);
                log?.Invoke("Gamepad scan failed: " + ex.Message);
            }
            return result;
        }

        // Hot-path controller check used by the monitor loop every 2 s.
        // Reuses the cached searcher to avoid repeated ManagementObjectSearcher creation.
        public static bool IsWatchedControllerConnected(IReadOnlyList<GamepadEntry> watched, Action<string> log = null)
        {
            if (watched == null || watched.Count == 0) return false;
            try
            {
                using (var items = GetSearcher().Get())
                {
                    foreach (ManagementObject mo in items)
                    using (mo)
                    {
                        string[] hwids = mo["HardwareID"] as string[] ?? Array.Empty<string>();
                        if (!hwids.Any(h => h.IndexOf(GamepadUsage, StringComparison.OrdinalIgnoreCase) >= 0))
                            continue;

                        string vidPid = hwids.Select(ExtractVidPid).FirstOrDefault(x => x != null);
                        if (vidPid == null) continue;

                        if (watched.Any(w => string.Equals(w.HardwareId, vidPid, StringComparison.OrdinalIgnoreCase)))
                            return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[SteamHelper] IsWatchedControllerConnected: " + ex.Message);
                log?.Invoke("Gamepad check failed: " + ex.Message);
                return false;
            }
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
