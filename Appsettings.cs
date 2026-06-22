// App settings: stored in registry HKCU\Software\SteamTV.
using System;
using Microsoft.Win32;

namespace SteamTV
{

    internal sealed class AppSettings
    {
        private const string RegPath = @"Software\SteamTV";

        public string TvIp = "192.168.1.102";
        public int TargetDisplay = 3;
        public bool DisableOthers = false;

        // Previously hardcoded in the script — keeping defaults here,
        // but can be changed directly in the registry without recompilation.
        public string AdbPath = @"C:\migrate\pc\steamTV\final3\platform-tools\adb.exe";
        public string SteamPath = @"C:\Program Files (x86)\Steam\steam.exe";

        // Android package names (as in the original script)
        public string HdmiSourcePackage = "com.liskovsoft.tvexternalsource.hdmi2";
        public string HdmiActivityPattern = @"com\.xiaomi\.mitv\.tvplayer/\.ExternalSourceActivity";
        public string TvHomeComponent = "com.spocky.projengmenu/.ui.home.MainActivity";

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
                    s.DisableOthers = ToInt(k.GetValue("DisableOthers"), 0) != 0;
                    s.AdbPath = (string)(k.GetValue("AdbPath") ?? s.AdbPath);
                    s.SteamPath = (string)(k.GetValue("SteamPath") ?? s.SteamPath);
                    s.HdmiSourcePackage = (string)(k.GetValue("HdmiSourcePackage") ?? s.HdmiSourcePackage);
                    s.HdmiActivityPattern = (string)(k.GetValue("HdmiActivityPattern") ?? s.HdmiActivityPattern);
                    s.TvHomeComponent = (string)(k.GetValue("TvHomeComponent") ?? s.TvHomeComponent);
                }
            }
            catch { /* invalid/missing settings — use defaults */ }
            return s;
        }

        public void Save()
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RegPath))
            {
                k.SetValue("TvIp", TvIp ?? "");
                k.SetValue("TargetDisplay", TargetDisplay, RegistryValueKind.DWord);
                k.SetValue("DisableOthers", DisableOthers ? 1 : 0, RegistryValueKind.DWord);
                k.SetValue("AdbPath", AdbPath ?? "");
                k.SetValue("SteamPath", SteamPath ?? "");
                k.SetValue("HdmiSourcePackage", HdmiSourcePackage ?? "");
                k.SetValue("HdmiActivityPattern", HdmiActivityPattern ?? "");
                k.SetValue("TvHomeComponent", TvHomeComponent ?? "");
            }
        }

        private static int ToInt(object v, int def)
        {
            if (v == null) return def;
            try { return Convert.ToInt32(v); } catch { return def; }
        }
    }

}