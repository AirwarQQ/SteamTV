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
            _adb = AdbBundle.Resolve(adbPath);
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

        // The power key (26) toggles, so it's only sent when the TV is known to be asleep — it used to
        // put the TV to sleep when its screensaver was running ("Dreaming") or dumpsys didn't answer.
        // Otherwise KEYCODE_WAKEUP (224), which never turns the screen off. (Not 224 for sleep too:
        // Xiaomi refuses it there — "wakeup reason is 6, can't wakeup device".)
        public string WakeTv() { bool asleep; return WakeTv(out asleep); }

        public string WakeTv(out bool wasAsleep)
        {
            Connect();
            string state = Wakefulness();
            wasAsleep = state == "Asleep" || state == "Dozing";
            if (state == "Awake") return "TV is already on.";
            Run("shell input keyevent " + (wasAsleep ? "26" : "224"));
            return wasAsleep ? "TV was asleep — woken up." : "Wake signal sent (TV state: " + (state ?? "unknown") + ").";
        }

        // "Awake", "Asleep", "Dreaming", "Dozing", or null when the TV didn't answer.
        public string Wakefulness()
        {
            string outp;
            if (!RunCapture("shell dumpsys power", out outp)) return null;
            var m = Regex.Match(outp, @"mWakefulness=(\w+)");
            return m.Success ? m.Groups[1].Value : null;
        }

        public void SleepTv() => Run("shell input keyevent 223");   // KEYCODE_SLEEP: never wakes

        // "device", "unauthorized" (the TV still shows the "Allow USB debugging?" prompt), "offline" or null.
        public string GetState()
        {
            Connect();
            string output;
            RunCapture("get-state", out output, 5000);
            // Errors read "error: device unauthorized." — check the specific words before "device".
            if (output.IndexOf("unauthorized", StringComparison.OrdinalIgnoreCase) >= 0) return "unauthorized";
            if (output.IndexOf("offline", StringComparison.OrdinalIgnoreCase) >= 0) return "offline";
            return Regex.IsMatch(output, @"(?m)^device\s*$") ? "device" : null;
        }

        public string GetModel()
        {
            string output;
            return RunCapture("shell getprop ro.product.model", out output) ? output.Trim() : null;
        }

        // The activity in front right now, as package/.Activity — to go back to it after the game.
        public string GetForegroundComponent()
        {
            string outp;
            if (!RunCapture("shell dumpsys activity activities", out outp)) return null;
            var m = Regex.Match(outp, @"(?m)^\s*ResumedActivity:? ActivityRecord\{\S+ u\d+ ([\w.]+/[\w.$]+)");
            return m.Success ? m.Groups[1].Value : null;
        }

        // HDMI ports whose input reports a connected cable (TvInputManager state 0). The hardware list
        // maps "id=6 … hdmi_port=2" to the input ".../HW6".
        public List<int> ConnectedHdmiPorts()
        {
            var ports = new List<int>();
            string outp;
            if (!RunCapture("shell dumpsys tv_input", out outp)) return ports;
            var state = new Dictionary<string, int>();
            foreach (Match m in Regex.Matches(outp, @"/HW(\d+): info: TvInputInfo\{[^}]*\}; state: (\d+)"))
                state[m.Groups[1].Value] = int.Parse(m.Groups[2].Value);
            foreach (Match m in Regex.Matches(outp, @"TvInputHardwareInfo \{id=(\d+),[^}]*\bhdmi_port=(\d+)"))
            {
                int s, port = int.Parse(m.Groups[2].Value);
                if (state.TryGetValue(m.Groups[1].Value, out s) && s == 0 && !ports.Contains(port)) ports.Add(port);
            }
            ports.Sort();
            return ports;
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

        public void RestoreSourceBeforeShutdown(string homeComponent, CancellationToken ct = default) =>
            ReturnTv(null, homeComponent, false, ct);

        // Leaves the PC's input: back to the app that was open before (its launcher entry resumes the
        // task where it was), else the configured launcher, else the Home key — then, if asked,
        // back to sleep. Returns what was done, for the log.
        public string ReturnTv(string previousComponent, string homeComponent, bool sleep, CancellationToken ct = default)
        {
            Connect();
            string done = null;
            string pkg = previousComponent?.Split('/')[0];
            if (!string.IsNullOrEmpty(pkg))
            {
                foreach (var category in new[] { "LEANBACK_LAUNCHER", "LAUNCHER" })
                {
                    string output;
                    RunCapture("shell monkey -p " + pkg + " -c android.intent.category." + category + " 1", out output);
                    if (output.IndexOf("Events injected: 1", StringComparison.Ordinal) >= 0) { done = "back to " + pkg; break; }
                }
            }
            if (done == null && !string.IsNullOrWhiteSpace(homeComponent) && StartActivity("-n " + homeComponent.Trim()))
                done = "launcher " + homeComponent.Trim();
            if (done == null) { Run("shell input keyevent 3"); done = "Home key"; }

            if (sleep)
            {
                ct.WaitHandle.WaitOne(1500);   // let the app come up first, so the TV wakes into it later
                SleepTv();
                done += ", then sleep";
            }
            Disconnect();
            ct.WaitHandle.WaitOne(1000);
            return done;
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
