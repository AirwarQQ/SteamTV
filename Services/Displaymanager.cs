// Enable/disable/restore monitors via CCD (QueryDisplayConfig/SetDisplayConfig).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SteamTV
{

    internal sealed class DisplayConfigSnapshot
    {
        public Native.DISPLAYCONFIG_PATH_INFO[] Paths;
        public Native.DISPLAYCONFIG_MODE_INFO[] Modes;
    }

    internal struct DisplayEntry
    {
        public int Number;          // \\.\DISPLAYn -> n (display number in "Screen Settings")
        public string FriendlyName;
        public bool Active;
    }

    internal static class DisplayManager
    {
        private static readonly Regex DisplayNumRe = new Regex(@"DISPLAY(\d+)", RegexOptions.IgnoreCase);

        // Read full current configuration (all paths, including disabled monitors).
        public static DisplayConfigSnapshot QueryAll(out string error)
        {
            error = null;
            uint numPaths, numModes;
            int r = Native.GetDisplayConfigBufferSizes(Native.QDC_ALL_PATHS, out numPaths, out numModes);
            if (r != Native.ERROR_SUCCESS) { error = "GetDisplayConfigBufferSizes=" + r; return null; }

            var paths = new Native.DISPLAYCONFIG_PATH_INFO[numPaths];
            var modes = new Native.DISPLAYCONFIG_MODE_INFO[numModes];
            r = Native.QueryDisplayConfig(Native.QDC_ALL_PATHS, ref numPaths, paths, ref numModes, modes, IntPtr.Zero);
            if (r != Native.ERROR_SUCCESS) { error = "QueryDisplayConfig=" + r; return null; }

            // shrink arrays to actual returned count
            if (numPaths != paths.Length) Array.Resize(ref paths, (int)numPaths);
            if (numModes != modes.Length) Array.Resize(ref modes, (int)numModes);
            return new DisplayConfigSnapshot { Paths = paths, Modes = modes };
        }

        private static string GetSourceGdiName(Native.LUID adapterId, uint sourceId)
        {
            var d = new Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME();
            d.header.type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
            d.header.size = (uint)Marshal.SizeOf(typeof(Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME));
            d.header.adapterId = adapterId;
            d.header.id = sourceId;
            return Native.DisplayConfigGetDeviceInfo(ref d) == Native.ERROR_SUCCESS ? d.viewGdiDeviceName : null;
        }

        private static string GetTargetFriendlyName(Native.LUID adapterId, uint targetId)
        {
            var d = new Native.DISPLAYCONFIG_TARGET_DEVICE_NAME();
            d.header.type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
            d.header.size = (uint)Marshal.SizeOf(typeof(Native.DISPLAYCONFIG_TARGET_DEVICE_NAME));
            d.header.adapterId = adapterId;
            d.header.id = targetId;
            return Native.DisplayConfigGetDeviceInfo(ref d) == Native.ERROR_SUCCESS ? d.monitorFriendlyDeviceName : null;
        }

        private static int NumberOf(Native.DISPLAYCONFIG_PATH_INFO p)
        {
            string name = GetSourceGdiName(p.sourceInfo.adapterId, p.sourceInfo.id);
            if (string.IsNullOrEmpty(name)) return -1;
            var m = DisplayNumRe.Match(name);
            return m.Success ? int.Parse(m.Groups[1].Value) : -1;
        }

        private static bool IsActive(Native.DISPLAYCONFIG_PATH_INFO p)
        {
            return (p.flags & Native.DISPLAYCONFIG_PATH_ACTIVE) != 0;
        }

        // List of monitors for the "Displays..." window
        public static List<DisplayEntry> ListDisplays()
        {
            var result = new Dictionary<int, DisplayEntry>();
            string err;
            var snap = QueryAll(out err);
            if (snap == null) return new List<DisplayEntry>();

            foreach (var p in snap.Paths)
            {
                int num = NumberOf(p);
                if (num < 0) continue;
                bool active = IsActive(p);
                string friendly = GetTargetFriendlyName(p.targetInfo.adapterId, p.targetInfo.id) ?? "";

                DisplayEntry existing;
                if (!result.TryGetValue(num, out existing))
                {
                    result[num] = new DisplayEntry { Number = num, FriendlyName = friendly, Active = active };
                }
                else if (active && !existing.Active)
                {
                    // active path takes priority for display
                    result[num] = new DisplayEntry { Number = num, FriendlyName = friendly, Active = true };
                }
            }
            return result.Values.OrderBy(e => e.Number).ToList();
        }

        private static bool Apply(Native.DISPLAYCONFIG_PATH_INFO[] paths, Native.DISPLAYCONFIG_MODE_INFO[] modes, uint flags, out string error)
        {
            int r = Native.SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, flags);
            error = r == Native.ERROR_SUCCESS ? null : ("SetDisplayConfig=" + r);
            return r == Native.ERROR_SUCCESS;
        }

        private const uint ApplyFlags =
            Native.SDC_APPLY | Native.SDC_USE_SUPPLIED_DISPLAY_CONFIG | Native.SDC_ALLOW_CHANGES | Native.SDC_SAVE_TO_DATABASE;

        public static bool EnableDisplay(int displayNumber, out string error)
        {
            error = null;
            var snap = QueryAll(out error);
            if (snap == null) return false;

            int idx = -1, idxAlready = -1;
            for (int i = 0; i < snap.Paths.Length; i++)
            {
                if (NumberOf(snap.Paths[i]) != displayNumber) continue;
                if (IsActive(snap.Paths[i])) { idxAlready = i; }
                else if (snap.Paths[i].targetInfo.targetAvailable != 0 && idx < 0) { idx = i; }
            }

            if (idxAlready >= 0 && idx < 0) return true; // already enabled
            if (idx < 0)
            {
                error = "Display #" + displayNumber + " not found among available paths.";
                return false;
            }

            snap.Paths[idx].flags |= Native.DISPLAYCONFIG_PATH_ACTIVE;
            return Apply(snap.Paths, snap.Modes, ApplyFlags, out error);
        }

        public static bool DisableDisplay(int displayNumber, out string error)
        {
            error = null;
            var snap = QueryAll(out error);
            if (snap == null) return false;

            bool changed = false;
            for (int i = 0; i < snap.Paths.Length; i++)
            {
                if (NumberOf(snap.Paths[i]) == displayNumber && IsActive(snap.Paths[i]))
                {
                    snap.Paths[i].flags &= ~Native.DISPLAYCONFIG_PATH_ACTIVE;
                    snap.Paths[i].sourceInfo.modeInfoIdx = Native.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
                    snap.Paths[i].targetInfo.modeInfoIdx = Native.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
                    changed = true;
                }
            }
            if (!changed) return true; // already disabled
            return Apply(snap.Paths, snap.Modes, ApplyFlags, out error);
        }

        // Make a specific display the primary display via ChangeDisplaySettingsEx.
        public static bool SetPrimaryDisplay(int displayNumber, out string error)
        {
            error = null;
            string deviceName = @"\\.\DISPLAY" + displayNumber;
            
            var devMode = new Native.DEVMODE();
            devMode.dmSize = (ushort)Marshal.SizeOf(typeof(Native.DEVMODE));
            devMode.dmSpecVersion = 0x0400; // DEVMODESPECVERSION
            devMode.dmFields = Native.DM_POSITION; // we're only setting position (required for CDS_SET_PRIMARY)
            
            int r = Native.ChangeDisplaySettingsEx(deviceName, ref devMode, IntPtr.Zero, Native.CDS_SET_PRIMARY | Native.CDS_UPDATEREGISTRY, IntPtr.Zero);
            if (r == Native.ERROR_SUCCESS)
            {
                return true;
            }
            error = "ChangeDisplaySettingsEx=" + r;
            return false;
        }

        // Keep only the target display active, disable all others one by one.
        public static bool DisableAllExcept(int displayNumber, out string error, Action<string> log = null)
        {
            error = null;
            
            // Collect unique active display numbers (excluding target)
            var snap = QueryAll(out error);
            if (snap == null) return false;
            
            var toDisable = new HashSet<int>();
            foreach (var p in snap.Paths)
            {
                if (!IsActive(p)) continue;
                int num = NumberOf(p);
                if (num < 0) continue;
                if (num == displayNumber) continue;
                toDisable.Add(num);
            }
            
            if (toDisable.Count == 0) { log?.Invoke("DisableAllExcept: nothing to disable."); return true; }
            
            log?.Invoke("DisableAllExcept: need to disable displays: " + string.Join(", ", toDisable));
            
            // First: make target display the primary (so we can disable the old primary)
            log?.Invoke("DisableAllExcept: making display #" + displayNumber + " primary first...");
            string errPrimary;
            SetPrimaryDisplay(displayNumber, out errPrimary);
            if (errPrimary != null)
            {
                log?.Invoke("DisableAllExcept: SetPrimaryDisplay warning: " + errPrimary + " (continuing anyway)");
            }
            
            // Disable one by one, re-querying after each to handle Windows restrictions
            foreach (int disp in toDisable)
            {
                log?.Invoke("DisableAllExcept: trying to disable display #" + disp + "...");
                string err2;
                if (DisableDisplay(disp, out err2))
                {
                    log?.Invoke("DisableAllExcept: display #" + disp + " disabled OK");
                }
                else
                {
                    log?.Invoke("DisableAllExcept: display #" + disp + " failed: " + err2 + " (skipping)");
                    // Don't fail completely — some displays (like primary) can't be disabled
                }
            }
            
            // Verify final state
            var finalSnap = QueryAll(out error);
            if (finalSnap != null)
            {
                var stillActive = new List<int>();
                foreach (var p in finalSnap.Paths)
                {
                    if (!IsActive(p)) continue;
                    int num = NumberOf(p);
                    if (num < 0 || num == displayNumber) continue;
                    if (!stillActive.Contains(num)) stillActive.Add(num);
                }
                if (stillActive.Count > 0)
                {
                    log?.Invoke("DisableAllExcept: still active (couldn't disable): " + string.Join(", ", stillActive));
                }
                else
                {
                    log?.Invoke("DisableAllExcept: all non-target displays disabled successfully.");
                }
            }
            
            return true; // partial success is acceptable
        }

        // Restore layout from snapshot (enable monitors back).
        public static bool RestoreAll(DisplayConfigSnapshot snap, out string error)
        {
            error = null;
            if (snap == null) { error = "No saved configuration."; return false; }

            // 1) exact restore
            if (Apply(snap.Paths, snap.Modes,
                    Native.SDC_APPLY | Native.SDC_USE_SUPPLIED_DISPLAY_CONFIG | Native.SDC_SAVE_TO_DATABASE, out error))
                return true;

            // 2) with mode modification allowed
            if (Apply(snap.Paths, snap.Modes, ApplyFlags, out error))
                return true;

            // 3) fallback — just "Extend to all monitors"
            int r = Native.SetDisplayConfig(0, null, 0, null, Native.SDC_APPLY | Native.SDC_TOPOLOGY_EXTEND);
            if (r == Native.ERROR_SUCCESS) { error = null; return true; }
            error = "RestoreAll fallback SetDisplayConfig=" + r;
            return false;
        }
    }

}