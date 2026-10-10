// App settings: stored in registry HKCU\Software\SteamTV.
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace SteamTV
{

    internal sealed class WatchedDevice
    {
        public string Key;        // DeviceScanner key: "VID_XXXX&PID_YYYY" or "BT_<MAC>" — stable across reboots
        public string Name;       // display label captured when the device was added
        public string Category;
        public bool Enabled = true;   // only enabled devices trigger activation
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

        // Replaced wholesale (never mutated in place) so the monitor thread can read it without locking.
        public List<WatchedDevice> WatchedDevices = new List<WatchedDevice>();

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

                    // REG_MULTI_SZ, one "Key|Enabled|Category|Name" per device (Name last: it may contain '|').
                    string[] raw = k.GetValue("WatchedDevices") as string[];
                    if (raw != null)
                    {
                        foreach (string line in raw)
                        {
                            var p = line.Split(new[] { '|' }, 4);
                            if (p.Length == 4 && p[0].Length > 0)
                                s.WatchedDevices.Add(new WatchedDevice
                                {
                                    Key = p[0], Enabled = p[1] != "0", Category = p[2], Name = p[3]
                                });
                        }
                    }
                    else if ((raw = k.GetValue("WatchedGamepads") as string[]) != null)
                    {
                        // Pre-1.6 format: "VID_XXXX&PID_YYYY|FriendlyName", gamepads only.
                        foreach (string line in raw)
                        {
                            int pipe = line.IndexOf('|');
                            if (pipe > 0)
                                s.WatchedDevices.Add(new WatchedDevice
                                {
                                    Key = line.Substring(0, pipe), Name = line.Substring(pipe + 1), Category = "Gamepad"
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

                    string[] raw = WatchedDevices
                        .Select(e => e.Key + "|" + (e.Enabled ? "1" : "0") + "|" + (e.Category ?? "") + "|" + (e.Name ?? ""))
                        .ToArray();
                    k.SetValue("WatchedDevices", raw, RegistryValueKind.MultiString);
                    k.DeleteValue("WatchedGamepads", false);
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
