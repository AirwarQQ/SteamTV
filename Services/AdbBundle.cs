// Extracts the embedded adb.exe to %LOCALAPPDATA%\SteamTV, so no separate platform-tools install
// is needed and it works even when SteamTV.exe sits somewhere read-only (Program Files). The copy
// is replaced whenever a newer SteamTV ships a different adb.exe.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;

namespace SteamTV
{
    internal static class AdbBundle
    {
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamTV");
        public static readonly string ExePath = Path.Combine(Dir, "adb.exe");

        public static void EnsureExtracted()
        {
            try
            {
                using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("SteamTV.adb.exe"))
                {
                    if (res == null) return;
                    byte[] bytes;
                    using (var ms = new MemoryStream()) { res.CopyTo(ms); bytes = ms.ToArray(); }
                    if (File.Exists(ExePath) && SameContent(ExePath, bytes)) return;

                    Directory.CreateDirectory(Dir);
                    try { File.WriteAllBytes(ExePath, bytes); }
                    catch (IOException)
                    {
                        // The old copy is still running as the adb server — stop it and retry once.
                        try { using (var p = Process.Start(new ProcessStartInfo(ExePath, "kill-server") { UseShellExecute = false, CreateNoWindow = true })) p?.WaitForExit(5000); }
                        catch { }
                        File.WriteAllBytes(ExePath, bytes);
                    }
                }
            }
            catch
            {
                // Best effort — Resolve() falls back to adb.exe next to SteamTV.exe or on PATH.
            }
        }

        // The "adb.exe" default (or a blank setting) means the bundled copy; anything else is the user's own.
        public static string Resolve(string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured) &&
                !string.Equals(configured.Trim(), "adb.exe", StringComparison.OrdinalIgnoreCase))
                return configured.Trim();
            return File.Exists(ExePath) ? ExePath : "adb.exe";
        }

        private static bool SameContent(string path, byte[] bytes)
        {
            if (new FileInfo(path).Length != bytes.Length) return false;
            using (var sha = SHA256.Create())
                return sha.ComputeHash(File.ReadAllBytes(path)).SequenceEqual(sha.ComputeHash(bytes));
        }
    }
}
