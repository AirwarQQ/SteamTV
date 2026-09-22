// Extracts the embedded adb.exe next to SteamTV.exe on first run, so no separate
// platform-tools install is needed. Windows resolves a bare "adb.exe" process name against
// the calling exe's own directory before PATH, so no path changes are needed elsewhere.
using System;
using System.IO;
using System.Reflection;

namespace SteamTV
{
    internal static class AdbBundle
    {
        public static void EnsureExtracted()
        {
            try
            {
                string dest = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "adb.exe");
                if (File.Exists(dest)) return;

                using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("SteamTV.adb.exe"))
                {
                    if (res == null) return;
                    using (var file = File.Create(dest))
                        res.CopyTo(file);
                }
            }
            catch
            {
                // Best effort — falls back to a system-installed adb.exe on PATH, same as before.
            }
        }
    }
}
