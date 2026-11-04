// Sleep / shut down / restart this PC on request from Home Assistant, plus the MAC address HA needs
// for Wake on LAN (switching on is the one thing an app on a switched-off PC can't do itself).
using System;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace SteamTV
{

    internal static class PowerControl
    {
        // Shutdown and restart show Windows' own countdown first, so whoever is at the PC can still
        // save their work or cancel with "shutdown /a".
        public const int ShutdownDelaySec = 30;

        // Returns null on success, otherwise why it failed.
        public static string Sleep()
        {
            if (!EnableShutdownPrivilege()) return "can't enable the shutdown privilege (error " + Marshal.GetLastWin32Error() + ")";
            // hibernate: false → S3 sleep, even where hibernation is switched on.
            return SetSuspendState(false, false, false) ? null : "SetSuspendState failed (error " + Marshal.GetLastWin32Error() + ")";
        }

        public static string Shutdown() => RunShutdownExe("/s", "Home Assistant is shutting this PC down.");
        public static string Restart() => RunShutdownExe("/r", "Home Assistant is restarting this PC.");

        private static string RunShutdownExe(string mode, string comment)
        {
            try
            {
                var psi = new ProcessStartInfo("shutdown.exe", mode + " /t " + ShutdownDelaySec + " /c \"" + comment + " Cancel: shutdown /a\"")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                };
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit(5000);
                    // 1190 = a shutdown is already scheduled — the PC goes down either way.
                    return p.HasExited && p.ExitCode != 0 && p.ExitCode != 1190 ? "shutdown.exe exit code " + p.ExitCode : null;
                }
            }
            catch (Exception ex) { return ex.Message; }
        }

        // MAC of the adapter that carries the default route — the one a Wake on LAN packet must reach.
        // Virtual adapters (Hyper-V, WSL, VPNs) have no gateway or aren't Ethernet/Wi-Fi, so they drop out.
        public static string PrimaryMac()
        {
            try
            {
                var nic = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up
                             && (n.NetworkInterfaceType == NetworkInterfaceType.Ethernet || n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                             && n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
                    .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : 1)   // WoL works on wired, rarely on Wi-Fi
                    .FirstOrDefault();
                var b = nic?.GetPhysicalAddress().GetAddressBytes();
                return b == null || b.Length != 6 ? null : string.Join(":", b.Select(x => x.ToString("X2")));
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------------

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

        // Every interactive user holds SeShutdownPrivilege, but it starts disabled in the token.
        private static bool EnableShutdownPrivilege()
        {
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr token)) return false;
            try
            {
                var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Attributes = SE_PRIVILEGE_ENABLED };
                if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out tp.Luid)) return false;
                return AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero) && Marshal.GetLastWin32Error() == 0;
            }
            finally { CloseHandle(token); }
        }

        private const int TOKEN_ADJUST_PRIVILEGES = 0x20, TOKEN_QUERY = 0x8, SE_PRIVILEGE_ENABLED = 2;

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct TOKEN_PRIVILEGES
        {
            public int PrivilegeCount;
            public long Luid;
            public int Attributes;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LookupPrivilegeValue(string system, string name, out long luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState,
            int bufferLength, IntPtr previousState, IntPtr returnLength);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }

}
