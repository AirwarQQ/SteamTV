// Wrapper around adb.exe: wake TV, switch HDMI source, restore source.
using System;
using System.Diagnostics;
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

        public void InstallApk(string path)
        {
            string output;
            if (!RunCapture("install -r \"" + path + "\"", out output, 120000) ||
                !Regex.IsMatch(output, @"(?m)^Success\s*$"))
                throw new InvalidOperationException("APK installation failed: " + output.Trim());
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

        public void SwitchSourceToPc(string hdmiPackage, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(hdmiPackage)) return;
            Connect();
            if (ct.WaitHandle.WaitOne(1000)) return;
            Run("shell monkey -p " + hdmiPackage + " -c android.intent.category.LAUNCHER 1");
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
