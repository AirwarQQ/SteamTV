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

    // Stable physical display identifier (does not change when DISPLAY numbers are reassigned)
    internal struct PhysicalDisplayId
    {
        public Native.LUID AdapterId;
        public uint TargetId;

        public override bool Equals(object obj)
        {
            if (!(obj is PhysicalDisplayId)) return false;
            var o = (PhysicalDisplayId)obj;
            return AdapterId.LowPart == o.AdapterId.LowPart
                && AdapterId.HighPart == o.AdapterId.HighPart
                && TargetId == o.TargetId;
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((int)AdapterId.LowPart * 397 ^ (int)AdapterId.HighPart) * 397 ^ (int)TargetId;
            }
        }
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
            return ApplyCompact(snap.Paths, snap.Modes, out error);
        }

        // Passes only active paths with a compact, re-indexed modes array to SetDisplayConfig.
        // This avoids ERROR_INVALID_PARAMETER (87) caused by orphaned mode entries — entries that
        // were referenced by disabled paths but remain in the array after those paths go inactive.
        // Also resets the source position to (0,0) when a single source remains, because Windows
        // requires the only (primary) display to be at the origin.
        private static bool ApplyCompact(
            Native.DISPLAYCONFIG_PATH_INFO[] allPaths,
            Native.DISPLAYCONFIG_MODE_INFO[] allModes,
            out string error)
        {
            var activePaths = allPaths.Where(p => IsActive(p)).ToArray();

            // Collect every mode index that is still referenced by an active path
            var usedIdx = new HashSet<uint>();
            foreach (var p in activePaths)
            {
                if (p.sourceInfo.modeInfoIdx != Native.DISPLAYCONFIG_PATH_MODE_IDX_INVALID
                    && p.sourceInfo.modeInfoIdx < (uint)allModes.Length)
                    usedIdx.Add(p.sourceInfo.modeInfoIdx);
                if (p.targetInfo.modeInfoIdx != Native.DISPLAYCONFIG_PATH_MODE_IDX_INVALID
                    && p.targetInfo.modeInfoIdx < (uint)allModes.Length)
                    usedIdx.Add(p.targetInfo.modeInfoIdx);
            }

            // Build compact modes array: only referenced entries, contiguous indices
            var oldToNew = new Dictionary<uint, uint>();
            var compactModes = new List<Native.DISPLAYCONFIG_MODE_INFO>();
            foreach (uint old in usedIdx.OrderBy(x => x))
            {
                oldToNew[old] = (uint)compactModes.Count;
                compactModes.Add(allModes[old]);
            }

            // A single remaining source mode must sit at (0,0) — Windows enforces this for primary
            int sourceCount = compactModes.Count(m => m.infoType == Native.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE);
            if (sourceCount == 1)
            {
                for (int i = 0; i < compactModes.Count; i++)
                {
                    if (compactModes[i].infoType != Native.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE) continue;
                    var m = compactModes[i];
                    m.modeUnion.sourceMode.position.x = 0;
                    m.modeUnion.sourceMode.position.y = 0;
                    compactModes[i] = m;
                    break;
                }
            }

            // Re-index paths so their modeInfoIdx values point into the compact array
            var remapped = new Native.DISPLAYCONFIG_PATH_INFO[activePaths.Length];
            for (int i = 0; i < activePaths.Length; i++)
            {
                remapped[i] = activePaths[i];
                remapped[i].sourceInfo.modeInfoIdx = RemapIdx(activePaths[i].sourceInfo.modeInfoIdx, oldToNew);
                remapped[i].targetInfo.modeInfoIdx = RemapIdx(activePaths[i].targetInfo.modeInfoIdx, oldToNew);
            }

            return Apply(remapped, compactModes.ToArray(), ApplyFlags, out error);
        }

        private static uint RemapIdx(uint idx, Dictionary<uint, uint> map)
        {
            if (idx == Native.DISPLAYCONFIG_PATH_MODE_IDX_INVALID) return idx;
            uint v;
            return map.TryGetValue(idx, out v) ? v : Native.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
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

        // Resolve a DISPLAY number to its stable physical identifier (adapterId + targetId).
        // Returns null if the display number is not found among available paths.
        public static PhysicalDisplayId? ResolvePhysicalId(int displayNumber, out string error)
        {
            error = null;
            var snap = QueryAll(out error);
            if (snap == null) return null;

            foreach (var p in snap.Paths)
            {
                if (NumberOf(p) == displayNumber && p.targetInfo.targetAvailable != 0)
                {
                    return new PhysicalDisplayId
                    {
                        AdapterId = p.targetInfo.adapterId,
                        TargetId = p.targetInfo.id
                    };
                }
            }
            error = "Display #" + displayNumber + " not found among available paths.";
            return null;
        }

        // Check if a path's target matches the given physical display ID.
        private static bool Matches(PhysicalDisplayId id, Native.DISPLAYCONFIG_PATH_INFO p)
        {
            return p.targetInfo.adapterId.LowPart == id.AdapterId.LowPart
                && p.targetInfo.adapterId.HighPart == id.AdapterId.HighPart
                && p.targetInfo.id == id.TargetId;
        }


        // Disable all active displays except the one identified by targetId, in a single SetDisplayConfig call.
        public static bool DisableAllExceptPhysical(PhysicalDisplayId targetId, out string error, Action<string> log = null)
        {
            error = null;
            var snap = QueryAll(out error);
            if (snap == null) return false;

            bool anyChanged = false;
            for (int i = 0; i < snap.Paths.Length; i++)
            {
                if (!IsActive(snap.Paths[i])) continue;
                if (Matches(targetId, snap.Paths[i])) continue;

                string name = GetTargetFriendlyName(
                    snap.Paths[i].targetInfo.adapterId, snap.Paths[i].targetInfo.id) ?? "(unknown)";
                log?.Invoke("DisableAllExceptPhysical: marking inactive: " + name);

                snap.Paths[i].flags &= ~Native.DISPLAYCONFIG_PATH_ACTIVE;
                snap.Paths[i].sourceInfo.modeInfoIdx = Native.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
                snap.Paths[i].targetInfo.modeInfoIdx = Native.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
                anyChanged = true;
            }

            if (!anyChanged)
            {
                log?.Invoke("DisableAllExceptPhysical: target is already the only active display.");
                return true;
            }

            log?.Invoke("DisableAllExceptPhysical: applying compact config...");
            bool ok = ApplyCompact(snap.Paths, snap.Modes, out error);
            log?.Invoke(ok ? "DisableAllExceptPhysical: done." : "DisableAllExceptPhysical: failed: " + error);
            return ok;
        }

        // Legacy method (kept for backward compatibility, uses DISPLAY numbers).
        public static bool DisableAllExcept(int displayNumber, out string error, Action<string> log = null)
        {
            error = null;
            var physId = ResolvePhysicalId(displayNumber, out error);
            if (physId == null) return false;
            return DisableAllExceptPhysical(physId.Value, out error, log);
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
            if (r == Native.ERROR_SUCCESS) { error = "WARNING: exact restore failed — monitors set to Extend (saved layout lost)."; return true; }
            error = "RestoreAll fallback SetDisplayConfig=" + r;
            return false;
        }
    }

}