// Wrapper around adb.exe: wake TV, switch HDMI source, restore source.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SteamTV
{

    internal sealed class Adb
    {
        private readonly string _adb;
        private readonly string _ip;

        public Adb(string adbPath, string ip)
        {
            _adb = adbPath;
            System.Net.IPAddress address;
            if (!System.Net.IPAddress.TryParse(ip, out address))
                throw new ArgumentException("Invalid TV IP address.");
            _ip = (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "[" + address + "]" : address.ToString()) + ":5555";
        }

        // run without reading output
        private void Run(string args, int timeoutMs = 8000)
        {
            string ignore;
            RunCapture(args, out ignore, timeoutMs);
        }

        // run with stdout capture
        private bool RunCapture(string args, out string output, int timeoutMs = 8000)
        {
            output = "";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _adb,
                    Arguments = (args.StartsWith("connect ") || args.StartsWith("disconnect ") ? "" : "-s \"" + _ip + "\" ") + args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return false;
                    var stdout = p.StandardOutput.ReadToEndAsync();
                    var stderr = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        output = "ADB timed out.";
                        return false;
                    }
                    output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
                    return p.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                output = "ADB ERROR: " + ex.Message;
                return false;
            }
        }

        public void Connect() => Run("connect " + _ip);
        public void Disconnect() => Run("disconnect " + _ip);

        public string DetectHomeComponent()
        {
            Connect();
            foreach (var resolver in new[] { "cmd package", "pm" })
            {
                string output;
                if (!RunCapture("shell " + resolver + " resolve-activity --brief --user current -a android.intent.action.MAIN -c android.intent.category.HOME", out output)) continue;
                var match = Regex.Match(output, @"(?m)^\s*([\w.]+/[\w.$]+)\s*$");
                if (match.Success && !match.Groups[1].Value.Contains("ResolverActivity"))
                    return match.Groups[1].Value;
            }
            throw new InvalidOperationException("No default TV launcher found. Select a default Home app on the TV, or enter its component manually.");
        }

        public bool IsPackageInstalled(string package)
        {
            string output;
            if (!RunCapture("shell pm list packages " + package, out output))
                throw new InvalidOperationException("Cannot check TV packages: " + output.Trim());
            return Regex.IsMatch(output, @"(?m)^package:" + Regex.Escape(package) + @"\s*$");
        }

        // Analog of Send-WOL: wake TV if screen is sleeping
        public string WakeTv()
        {
            Connect();
            string outp;
            RunCapture("shell dumpsys power", out outp);
            bool awake = outp.IndexOf("mWakefulness=Awake", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!awake)
            {
                Run("shell input keyevent 26"); // power
                return "TV was sleeping — wake signal sent.";
            }
            return "TV is already on.";
        }

        private const string XiaomiPlayer = "com.xiaomi.mitv.tvplayer";

        // Switches the TV to HDMI <port> with nothing installed on the TV. Tried in order: Xiaomi's
        // built-in player, the standard Android TV passthrough intent, then the legacy liskovsoft
        // "TV External Source" app if it is still installed. Returns a regex for the activity that
        // shows the input (null when it can't be known), or throws if no method works.
        public string SwitchToHdmi(int port, out string method, CancellationToken ct = default)
        {
            method = null;
            Connect();
            if (ct.WaitHandle.WaitOne(1000)) return null;

            if (IsPackageInstalled(XiaomiPlayer))
            {
                // Xiaomi numbers HDMI sources from 23 (the player logs them as hdmiN_order);
                // without a valid "input" extra it silently reopens the last used source.
                const string component = XiaomiPlayer + "/.ExternalSourceActivity";
                if (StartActivity("-a " + XiaomiPlayer + ".EXTSRC_PLAY -n " + component + " --ei input " + (22 + port)))
                {
                    method = "Xiaomi TV player";
                    return Regex.Escape(component);
                }
            }

            string inputId = FindHdmiInputId(port);
            if (inputId != null)
            {
                string intent = "-a android.intent.action.VIEW -d content://android.media.tv/passthrough/" + Uri.EscapeDataString(inputId);
                string handler = ResolveActivity(intent);
                if (handler != null && StartActivity(intent))
                {
                    method = "Android TV input " + inputId;
                    return Regex.Escape(handler.Split('/')[0]) + "/";
                }
            }

            string legacyApp = "com.liskovsoft.tvexternalsource.hdmi" + port;
            if (IsPackageInstalled(legacyApp))
            {
                Run("shell monkey -p " + legacyApp + " -c android.intent.category.LAUNCHER 1");
                method = "TV External Source app";
                return null;
            }

            throw new InvalidOperationException("This TV doesn't accept HDMI switching over ADB (no Xiaomi player, no Android TV input for HDMI " + port + ").");
        }

        // Input id from 'dumpsys tv_input' for a physical HDMI port. The hardware list carries
        // "id=6, ... hdmi_port=2"; the matching input id ends with "/HW6" (MediaTek, Amlogic, ...).
        // Vendors that name inputs differently fall back to the HDMI inputs in hardware order.
        private string FindHdmiInputId(int port)
        {
            string outp;
            if (!RunCapture("shell dumpsys tv_input", out outp)) return null;
            var inputs = new List<string>();
            foreach (Match m in Regex.Matches(outp, @"TvInputInfo\{id=([^,\s]+)"))
                if (!inputs.Contains(m.Groups[1].Value)) inputs.Add(m.Groups[1].Value);

            foreach (Match m in Regex.Matches(outp, @"TvInputHardwareInfo \{id=(\d+),[^}]*\bhdmi_port=(\d+)"))
            {
                if (m.Groups[2].Value != port.ToString()) continue;
                string hit = inputs.FirstOrDefault(id => id.EndsWith("/HW" + m.Groups[1].Value, StringComparison.Ordinal));
                if (hit != null) return hit;
            }

            var hdmi = inputs.Where(id => id.IndexOf("hdmi", StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(id => { var n = Regex.Match(id, @"(\d+)$"); return n.Success ? int.Parse(n.Value) : int.MaxValue; })
                .ToList();
            return port <= hdmi.Count ? hdmi[port - 1] : null;
        }

        private string ResolveActivity(string intentArgs)
        {
            string output;
            if (!RunCapture("shell cmd package resolve-activity --brief " + intentArgs, out output)) return null;
            var match = Regex.Match(output, @"(?m)^\s*([\w.]+/[\w.$]+)\s*$");
            return match.Success ? match.Groups[1].Value : null;
        }

        // 'am start' exits 0 even when nothing handles the intent; the failure is only in its output.
        private bool StartActivity(string intentArgs)
        {
            string output;
            return RunCapture("shell am start " + intentArgs, out output) &&
                   output.IndexOf("Error", StringComparison.OrdinalIgnoreCase) < 0;
        }

        public bool IsHdmiSourceActive(string activityPattern)
        {
            string outp;
            RunCapture("shell dumpsys activity activities", out outp);
            if (string.IsNullOrEmpty(outp)) return false;
            var re = new Regex(activityPattern, RegexOptions.IgnoreCase);
            foreach (var line in outp.Split('\n'))
            {
                if (line.IndexOf("ResumedActivity", StringComparison.OrdinalIgnoreCase) >= 0 && re.IsMatch(line))
                    return true;
            }
            return false;
        }

        public void RestoreSourceBeforeShutdown(string homeComponent, CancellationToken ct = default)
        {
            Run("shell am start -n " + homeComponent);
            Disconnect();
            ct.WaitHandle.WaitOne(1000);
        }

        // Returns true if ADB is reachable and the device accepts the connection.
        // Safe to call from a background thread.
        public bool IsAdbReachable(int timeoutMs = 4000)
        {
            string output;
            RunCapture("connect " + _ip, out output, timeoutMs);
            return output.IndexOf("connected to", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

}
