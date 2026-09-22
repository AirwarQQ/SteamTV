// App settings: stored in registry HKCU\Software\SteamTV.
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace SteamTV
{

    internal sealed class GamepadEntry
    {
        public string HardwareId;    // canonical "VID_XXXX&PID_YYYY" — stable across reboots
        public string FriendlyName;  // display label (name at time of adding)

        public override string ToString() =>
            string.IsNullOrEmpty(FriendlyName) ? HardwareId
                : FriendlyName + "  (" + HardwareId + ")";
    }

    internal sealed class AppSettings
    {
        private const string RegPath = @"Software\SteamTV";

        public string TvIp = "192.168.1.102";
        public int TargetDisplay = 3;
        public int HdmiPort = 2;         // 1–4; maps to com.liskovsoft.tvexternalsource.hdmiN
        public bool DisableOthers = false;
        public bool EnableWakeTV = true;
        public bool EnableSourceSwitch = true;
        public bool EnableBigPicture = true;
        public bool AutoMonitorOnStart = true;   // start monitoring immediately on autostart

        public string AdbPath = @"adb.exe";
        public string SteamPath = @"C:\Program Files (x86)\Steam\steam.exe";
        public string HdmiActivityPattern = @"com\.xiaomi\.mitv\.tvplayer/\.ExternalSourceActivity";
        public string TvHomeComponent = "com.spocky.projengmenu/.ui.home.MainActivity";

        public List<GamepadEntry> WatchedGamepads = new List<GamepadEntry>();

        // Derived from HdmiPort — no manual override needed for the standard liskovsoft app.
        public string HdmiSourcePackage => "com.liskovsoft.tvexternalsource.hdmi" + HdmiPort;

        public static AppSettings Load()
        {
            var s = new AppSettings();
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RegPath))
                {
                    if (k == null) return s;
                    s.TvIp = (string)(k.GetValue("TvIp") ?? s.TvIp);
                    s.TargetDisplay = ToInt(k.GetValue("TargetDisplay"), s.TargetDisplay);
                    s.HdmiPort = Math.Max(1, Math.Min(4, ToInt(k.GetValue("HdmiPort"), s.HdmiPort)));
                    s.DisableOthers = ToInt(k.GetValue("DisableOthers"), 0) != 0;
                    s.EnableWakeTV = ToInt(k.GetValue("EnableWakeTV"), 1) != 0;
                    s.EnableSourceSwitch = ToInt(k.GetValue("EnableSourceSwitch"), 1) != 0;
                    s.EnableBigPicture = ToInt(k.GetValue("EnableBigPicture"), 1) != 0;
                    s.AutoMonitorOnStart = ToInt(k.GetValue("AutoMonitorOnStart"), 1) != 0;
                    s.AdbPath = (string)(k.GetValue("AdbPath") ?? s.AdbPath);
                    s.SteamPath = (string)(k.GetValue("SteamPath") ?? s.SteamPath);
                    s.HdmiActivityPattern = (string)(k.GetValue("HdmiActivityPattern") ?? s.HdmiActivityPattern);
                    s.TvHomeComponent = (string)(k.GetValue("TvHomeComponent") ?? s.TvHomeComponent);

                    // Watched gamepads stored as REG_MULTI_SZ; each entry: "HWID|FriendlyName"
                    string[] raw = k.GetValue("WatchedGamepads") as string[];
                    if (raw != null)
                    {
                        foreach (string line in raw)
                        {
                            int pipe = line.IndexOf('|');
                            if (pipe > 0)
                                s.WatchedGamepads.Add(new GamepadEntry
                                {
                                    HardwareId = line.Substring(0, pipe),
                                    FriendlyName = line.Substring(pipe + 1)
                                });
                        }
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine("[AppSettings] Load error: " + ex.Message); }
            return s;
        }

        public void Save()
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RegPath))
                {
                    k.SetValue("TvIp", TvIp ?? "");
                    k.SetValue("TargetDisplay", TargetDisplay, RegistryValueKind.DWord);
                    k.SetValue("HdmiPort", HdmiPort, RegistryValueKind.DWord);
                    k.SetValue("DisableOthers", DisableOthers ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("EnableWakeTV", EnableWakeTV ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("EnableSourceSwitch", EnableSourceSwitch ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("EnableBigPicture", EnableBigPicture ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("AutoMonitorOnStart", AutoMonitorOnStart ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("AdbPath", AdbPath ?? "");
                    k.SetValue("SteamPath", SteamPath ?? "");
                    k.SetValue("HdmiActivityPattern", HdmiActivityPattern ?? "");
                    k.SetValue("TvHomeComponent", TvHomeComponent ?? "");

                    string[] raw = WatchedGamepads
                        .Select(e => (e.HardwareId ?? "") + "|" + (e.FriendlyName ?? ""))
                        .ToArray();
                    k.SetValue("WatchedGamepads", raw, RegistryValueKind.MultiString);
                }
            }
            catch (Exception ex) { Console.WriteLine("[AppSettings] Save error: " + ex.Message); }
        }

        private static int ToInt(object v, int def)
        {
            if (v == null) return def;
            try { return Convert.ToInt32(v); } catch { return def; }
        }
    }

}
