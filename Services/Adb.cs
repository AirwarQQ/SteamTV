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

        public Adb(string adbPath, string ip) { _adb = adbPath; _ip = ip; }

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
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return false;
                    string outStr = p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        output = outStr;
                        return false;
                    }
                    output = outStr;
                    return true;
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

        public void SwitchSourceToPc(string hdmiPackage)
        {
            Connect();
            Thread.Sleep(1000);
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

        public void RestoreSourceBeforeShutdown(string homeComponent)
        {
            Run("shell am start -n " + homeComponent);
            Disconnect();
            Thread.Sleep(1000);
        }
    }

}