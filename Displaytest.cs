// DisplayTest — console tool for testing monitor on/off switching.
//
// Calls the REAL application functions (DisplayManager.EnableDisplay / DisableDisplay),
// so if it works here — it will work in SteamTV too. Also prints the "raw" CCD path
// table to see exactly how a disconnected TV looks to Windows.
//
// Build (from project root, .NET Framework only — csc is included in Windows):
//   %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:exe /out:DisplayTest.exe ^
//     Tools\DisplayTest.cs Interop\Native.cs Services\DisplayManager.cs
//   (or just run build-displaytest.bat)
//
// Usage:
//   DisplayTest            show display table (as seen by Windows)
//   DisplayTest list       same
//   DisplayTest on  <N>    turn on display #N (number as in "Screen Settings")
//   DisplayTest off <N>    turn off display #N
//
// Note: on/off may cause the screen to flicker/rebuild — this is normal.

using System;
using System.Runtime.InteropServices;

namespace SteamTV
{
    internal static class DisplayTest
    {
        private static int Main(string[] args)
        {
            try
            {
                string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "list";

                if (cmd == "list" || cmd == "dump")
                {
                    DumpRaw();
                    Console.WriteLine("--- Summary (number / state / name) ---");
                    DumpFriendly();
                    return 0;
                }

                if (cmd == "on" || cmd == "off")
                {
                    int n;
                    if (args.Length < 2 || !int.TryParse(args[1], out n))
                    {
                        Console.WriteLine("Specify display number, e.g.:  DisplayTest " + cmd + " 3");
                        return 2;
                    }

                    Console.WriteLine("=== BEFORE ===");
                    DumpFriendly();
                    Console.WriteLine();

                    bool ok;
                    string err;
                    if (cmd == "on")
                    {
                        Console.WriteLine("Turning on display #" + n + " ...");
                        ok = DisplayManager.EnableDisplay(n, out err);
                    }
                    else
                    {
                        Console.WriteLine("Turning off display #" + n + " ...");
                        ok = DisplayManager.DisableDisplay(n, out err);
                    }

                    if (ok)
                        Console.WriteLine("RESULT: success.");
                    else
                        Console.WriteLine("RESULT: ERROR — " + Decode(err));

                    Console.WriteLine();
                    Console.WriteLine("=== AFTER ===");
                    DumpFriendly();
                    return ok ? 0 : 1;
                }

                Console.WriteLine("Commands:  list  |  on <N>  |  off <N>");
                return 2;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Exception: " + ex);
                return 3;
            }
        }

        // Human-readable summary — same as the "Displays..." button in the app.
        private static void DumpFriendly()
        {
            var list = DisplayManager.ListDisplays();
            if (list.Count == 0)
            {
                Console.WriteLine("  (no displays found)");
                return;
            }
            foreach (var d in list)
            {
                string name = string.IsNullOrEmpty(d.FriendlyName) ? "(name unavailable)" : d.FriendlyName;
                Console.WriteLine("  #" + d.Number + "   " + (d.Active ? "[active] " : "[off]") + "   " + name);
            }
        }

        // Full CCD path table (including inactive) — for diagnostics.
        private static void DumpRaw()
        {
            string err;
            var snap = DisplayManager.QueryAll(out err);
            if (snap == null)
            {
                Console.WriteLine("QueryAll: " + Decode(err));
                return;
            }

            Console.WriteLine("Paths: " + snap.Paths.Length + "   Modes: " + snap.Modes.Length);
            Console.WriteLine("idx  active avail  srcId tgtId  source        monitor");
            Console.WriteLine("---  ------ -----  ----- -----  ------------  -------------------------");
            for (int i = 0; i < snap.Paths.Length; i++)
            {
                var p = snap.Paths[i];
                bool active = (p.flags & Native.DISPLAYCONFIG_PATH_ACTIVE) != 0;
                bool avail = p.targetInfo.targetAvailable != 0;
                string src = GetSourceName(p.sourceInfo.adapterId, p.sourceInfo.id);
                string mon = GetTargetName(p.targetInfo.adapterId, p.targetInfo.id);

                Console.WriteLine(
                    Pad(i.ToString(), 3) + "  " +
                    Pad(active ? "yes" : "no", 6) + " " +
                    Pad(avail ? "yes" : "no", 5) + "  " +
                    Pad(p.sourceInfo.id.ToString(), 5) + " " +
                    Pad(p.targetInfo.id.ToString(), 5) + "  " +
                    Pad(src == null ? "-" : src, 12) + "  " +
                    (mon == null ? "-" : mon));
            }
            Console.WriteLine();
        }

        private static string GetSourceName(Native.LUID adapter, uint id)
        {
            var d = new Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME();
            d.header.type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
            d.header.size = (uint)Marshal.SizeOf(typeof(Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME));
            d.header.adapterId = adapter;
            d.header.id = id;
            if (Native.DisplayConfigGetDeviceInfo(ref d) != Native.ERROR_SUCCESS) return null;
            string name = d.viewGdiDeviceName == null ? "" : d.viewGdiDeviceName;
            int k = name.LastIndexOf('\\');           // keep tail \\.\DISPLAYn
            return k >= 0 ? name.Substring(k + 1) : name;
        }

        private static string GetTargetName(Native.LUID adapter, uint id)
        {
            var d = new Native.DISPLAYCONFIG_TARGET_DEVICE_NAME();
            d.header.type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
            d.header.size = (uint)Marshal.SizeOf(typeof(Native.DISPLAYCONFIG_TARGET_DEVICE_NAME));
            d.header.adapterId = adapter;
            d.header.id = id;
            if (Native.DisplayConfigGetDeviceInfo(ref d) != Native.ERROR_SUCCESS) return null;
            return d.monitorFriendlyDeviceName;
        }

        // Decode error string like "SetDisplayConfig=87" into human-readable Win32 code.
        private static string Decode(string err)
        {
            if (string.IsNullOrEmpty(err)) return "(empty)";
            int eq = err.LastIndexOf('=');
            int code;
            if (eq >= 0 && int.TryParse(err.Substring(eq + 1).Trim(), out code))
            {
                string name;
                switch (code)
                {
                    case 0: name = "ERROR_SUCCESS"; break;
                    case 31: name = "ERROR_GEN_FAILURE — device not responding"; break;
                    case 87: name = "ERROR_INVALID_PARAMETER — invalid paths/modes ('Invalid paths information')"; break;
                    case 1004: name = "ERROR_INVALID_FLAGS"; break;
                    case 1168: name = "ERROR_NOT_FOUND"; break;
                    case 1359: name = "ERROR_INTERNAL_ERROR"; break;
                    default: name = "Win32 code " + code; break;
                }
                return err + "  →  " + name;
            }
            return err;
        }

        private static string Pad(string s, int width)
        {
            if (s == null) s = "";
            if (s.Length >= width) return s;
            return s + new string(' ', width - s.Length);
        }
    }
}