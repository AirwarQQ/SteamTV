// App settings: stored in registry HKCU\Software\SteamTV.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace SteamTV
{

    internal enum TriggerAction
    {
        TvAndBigPicture = 0,   // full sequence (Big Picture still subject to the global toggle)
        TvOnly = 1,            // wake + displays + HDMI source, but no Big Picture
    }

    internal sealed class WatchedDevice
    {
        public string Key;        // DeviceScanner key: "VID_XXXX&PID_YYYY" or "BT_<MAC>" — stable across reboots
        public string Name;       // product name; refreshed from live scans
        public string Alias;      // user's own name; never overwritten by scans
        public string Category;
        public bool Enabled = true;   // only enabled devices trigger activation
        public TriggerAction Action = TriggerAction.TvAndBigPicture;
        public bool RestoreOnDisconnect;   // deactivate once this device goes away

        public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? Name : Alias;
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

        // Home Assistant over MQTT — off by default; when off nothing MQTT-related runs at all.
        public bool MqttEnabled = false;
        public string MqttHost = "";
        public int MqttPort = 1883;
        public string MqttUser = "";
        public string MqttPassword = "";   // kept DPAPI-encrypted (current user) in the registry
        public string MqttDeviceId = MakeDeviceId(Environment.MachineName);   // steamtv/<id>/... topics

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
                    s.MqttEnabled = ToInt(k.GetValue("MqttEnabled"), 0) != 0;
                    s.MqttHost = (string)(k.GetValue("MqttHost") ?? s.MqttHost);
                    s.MqttPort = ToInt(k.GetValue("MqttPort"), s.MqttPort);
                    s.MqttUser = (string)(k.GetValue("MqttUser") ?? s.MqttUser);
                    s.MqttPassword = Unprotect(k.GetValue("MqttPassword") as byte[]);
                    s.MqttDeviceId = MakeDeviceId((string)k.GetValue("MqttDeviceId") ?? s.MqttDeviceId);

                    // REG_MULTI_SZ, one device per line:
                    //   v2: "v2|Key|Enabled|Category|Action|RestoreOnDisconnect|Alias|Name" (Alias/Name URI-escaped)
                    //   v1 (1.6.0): "Key|Enabled|Category|Name" (Name last, unescaped — may contain '|')
                    string[] raw = k.GetValue("WatchedDevices") as string[];
                    if (raw != null)
                    {
                        foreach (string line in raw)
                        {
                            if (line.StartsWith("v2|", StringComparison.Ordinal))
                            {
                                var p = line.Split('|');
                                if (p.Length == 8 && p[1].Length > 0)
                                    s.WatchedDevices.Add(new WatchedDevice
                                    {
                                        Key = p[1], Enabled = p[2] != "0", Category = p[3],
                                        Action = p[4] == "1" ? TriggerAction.TvOnly : TriggerAction.TvAndBigPicture,
                                        RestoreOnDisconnect = p[5] == "1",
                                        Alias = Uri.UnescapeDataString(p[6]), Name = Uri.UnescapeDataString(p[7])
                                    });
                            }
                            else
                            {
                                var p = line.Split(new[] { '|' }, 4);
                                if (p.Length == 4 && p[0].Length > 0)
                                    s.WatchedDevices.Add(new WatchedDevice
                                    {
                                        Key = p[0], Enabled = p[1] != "0", Category = p[2], Name = p[3]
                                    });
                            }
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
                    k.SetValue("MqttEnabled", MqttEnabled ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("MqttHost", MqttHost ?? "");
                    k.SetValue("MqttPort", MqttPort, RegistryValueKind.DWord);
                    k.SetValue("MqttUser", MqttUser ?? "");
                    k.SetValue("MqttPassword", Protect(MqttPassword), RegistryValueKind.Binary);
                    k.SetValue("MqttDeviceId", MqttDeviceId ?? "");

                    string[] raw = WatchedDevices
                        .Select(e => string.Join("|",
                            "v2", e.Key, e.Enabled ? "1" : "0", e.Category ?? "",
                            e.Action == TriggerAction.TvOnly ? "1" : "0", e.RestoreOnDisconnect ? "1" : "0",
                            Esc(e.Alias), Esc(e.Name)))
                        .ToArray();
                    k.SetValue("WatchedDevices", raw, RegistryValueKind.MultiString);
                    k.DeleteValue("WatchedGamepads", false);
                }
            }
            catch (Exception ex) { Console.WriteLine("[AppSettings] Save error: " + ex.Message); }
        }

        // Only the separator and the escape char itself — keeps names readable in regedit
        // (full URI escaping turned "Пульт" into %D0%9F...). Uri.UnescapeDataString reverses it.
        private static string Esc(string s) => (s ?? "").Replace("%", "%25").Replace("|", "%7C");

        // MQTT topic segment: lowercase letters, digits, '_' and '-' only ('+', '#', '/' would break topics).
        public static string MakeDeviceId(string raw)
        {
            var id = new string((raw ?? "").ToLowerInvariant()
                .Select(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' ? c : '_').ToArray()).Trim('_');
            return id.Length > 0 ? id : "pc";
        }

        private static byte[] Protect(string s)
        {
            if (string.IsNullOrEmpty(s)) return new byte[0];
            return ProtectedData.Protect(Encoding.UTF8.GetBytes(s), null, DataProtectionScope.CurrentUser);
        }

        private static string Unprotect(byte[] b)
        {
            if (b == null || b.Length == 0) return "";
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(b, null, DataProtectionScope.CurrentUser)); }
            catch { return ""; }   // copied from another user/PC — can't be decrypted, ask again
        }

        private static int ToInt(object v, int def)
        {
            if (v == null) return def;
            try { return Convert.ToInt32(v); } catch { return def; }
        }
    }

}
