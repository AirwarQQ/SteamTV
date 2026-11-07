// Extracts the embedded adb.exe (and the two DLLs it needs) to %LOCALAPPDATA%\NekoCouch, so no separate platform-tools install
// is needed and it works even when NekoCouch.exe sits somewhere read-only (Program Files). The copy
// is replaced whenever a newer NekoCouch ships a different adb.exe.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;

namespace NekoCouch
{
    internal static class AdbBundle
    {
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NekoCouch");
        public static readonly string ExePath = Path.Combine(Dir, "adb.exe");

        // adb.exe won't start without its two DLLs next to it ("AdbWinApi.dll was not found") — on a
        // PC with platform-tools on PATH it happens to find them there, elsewhere it doesn't.
        private static readonly string[] Files = { "AdbWinApi.dll", "AdbWinUsbApi.dll", "adb.exe" };

        public static void EnsureExtracted()
        {
            bool serverStopped = false;
            foreach (string name in Files)
            {
                try
                {
                    byte[] bytes;
                    using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("NekoCouch." + name))
                    {
                        if (res == null) continue;
                        using (var ms = new MemoryStream()) { res.CopyTo(ms); bytes = ms.ToArray(); }
                    }
                    string path = Path.Combine(Dir, name);
                    if (File.Exists(path) && SameContent(path, bytes)) continue;

                    Directory.CreateDirectory(Dir);
                    try { File.WriteAllBytes(path, bytes); }
                    catch (IOException) when (!serverStopped)
                    {
                        // The old copy is still running as the adb server — stop it and retry once.
                        serverStopped = true;
                        try { using (var p = Process.Start(new ProcessStartInfo(ExePath, "kill-server") { UseShellExecute = false, CreateNoWindow = true })) p?.WaitForExit(5000); }
                        catch { }
                        File.WriteAllBytes(path, bytes);
                    }
                }
                catch
                {
                    // Best effort — Resolve() falls back to adb.exe next to NekoCouch.exe or on PATH.
                }
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
