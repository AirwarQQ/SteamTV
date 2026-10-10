// Enumerates user-pluggable devices (USB + Bluetooth) via cfgmgr32 and groups their devnodes
// into physical devices with a stable key, a readable name, a category and a connected flag.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SteamTV
{

    internal sealed class ExternalDevice
    {
        public string Key;        // "VID_XXXX&PID_YYYY", or "BT_<MAC>" for Bluetooth devices without a VID/PID
        public string Name;
        public string Category;
        public string Bus;        // "USB" or "Bluetooth"
        public bool IsConnected;  // false only for paired-but-not-connected Bluetooth devices
    }

    internal static class DeviceScanner
    {
        public static readonly string[] CategoryOrder =
            { "Gamepad", "Keyboard", "Keyboard + Mouse", "Mouse", "Audio", "Camera", "Storage", "Other" };

        // Every devnode built into the PC itself shares this container; anything else is external.
        private static readonly Guid LocalMachineContainer = new Guid("00000000-0000-0000-ffff-ffffffffffff");

        private static readonly Regex UsbVidPid =
            new Regex(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Bluetooth HID ids carry a vendor-source prefix: "..._VID&0002057e_PID&2009" (classic) or "VID&02045e" (LE).
        private static readonly Regex BtVidPid =
            new Regex(@"VID&[0-9A-F]*?([0-9A-F]{4})_PID&([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex BtMac =
            new Regex(@"^BTH(?:ENUM|LE)\\DEV_([0-9A-F]{12})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private sealed class Node
        {
            public string Id, Class, Name, BusName, VidPid;
            public string[] HwIds, CompatIds;
            public Guid Container;
            public bool? BtConnected;   // set only on the Bluetooth device node (BTHENUM\DEV_ / BTHLE\DEV_)
            public bool IsBtDeviceNode => BtConnected.HasValue;
        }

        public static string NormalizeVidPid(string anyId)
        {
            if (string.IsNullOrEmpty(anyId)) return null;
            var m = UsbVidPid.Match(anyId);
            if (!m.Success) m = BtVidPid.Match(anyId);
            if (!m.Success) return null;
            return "VID_" + m.Groups[1].Value.ToUpperInvariant() + "&PID_" + m.Groups[2].Value.ToUpperInvariant();
        }

        public static List<ExternalDevice> Scan()
        {
            var nodes = new List<Node>();
            foreach (string id in GetPresentDeviceIds())
            {
                int devInst;
                if (CM_Locate_DevNodeW(out devInst, id, 0) != CR_SUCCESS) continue;

                Guid container = GetGuid(devInst, ContainerIdKey);
                if (container == Guid.Empty || container == LocalMachineContainer) continue;

                var n = new Node
                {
                    Id = id,
                    Container = container,
                    Class = GetString(devInst, ClassKey) ?? "",
                    Name = GetString(devInst, FriendlyNameKey) ?? GetString(devInst, DeviceDescKey) ?? "",
                    BusName = GetString(devInst, BusReportedDescKey),
                    HwIds = GetStringList(devInst, HardwareIdsKey),
                    CompatIds = GetStringList(devInst, CompatibleIdsKey),
                };
                n.VidPid = NormalizeVidPid(id) ?? n.HwIds.Select(NormalizeVidPid).FirstOrDefault(v => v != null);
                if (BtMac.IsMatch(id))
                    n.BtConnected = GetBool(devInst, BtIsConnectedKey) ?? false;
                nodes.Add(n);
            }
            return Group(nodes);
        }

        private static List<ExternalDevice> Group(List<Node> nodes)
        {
            // Primary grouping is by VID/PID: that's the identity that survives replugging and reboots.
            var groups = new Dictionary<string, List<Node>>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in nodes.Where(n => n.VidPid != null))
                Bucket(groups, n.VidPid).Add(n);

            // Bluetooth devices with no VID/PID anywhere (phones, headphones) are keyed by MAC instead.
            var containersWithVidPid = new HashSet<Guid>(nodes.Where(n => n.VidPid != null).Select(n => n.Container));
            foreach (var bt in nodes.Where(n => n.IsBtDeviceNode && !containersWithVidPid.Contains(n.Container)))
                Bucket(groups, "BT_" + BtMac.Match(bt.Id).Groups[1].Value.ToUpperInvariant()).Add(bt);

            // Remaining nodes (BT device node, disk/volume/audio-endpoint children) carry no VID/PID —
            // attach them to their physical device through the container, when that's unambiguous.
            var byContainer = groups
                .SelectMany(g => g.Value.Select(n => new { n.Container, g.Key }))
                .Distinct()
                .ToLookup(x => x.Container, x => x.Key);
            foreach (var n in nodes.Where(n => n.VidPid == null))
            {
                var owners = byContainer[n.Container].ToList();
                if (owners.Count == 1 && !groups[owners[0]].Contains(n))
                    groups[owners[0]].Add(n);
            }

            var result = new List<ExternalDevice>();
            foreach (var g in groups)
            {
                var d = Describe(g.Key, g.Value);
                if (d != null) result.Add(d);
            }
            return result
                .OrderBy(d => Array.IndexOf(CategoryOrder, d.Category))
                .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static ExternalDevice Describe(string key, List<Node> g)
        {
            // The PC's own Bluetooth radio is a USB device too — not something anyone "plugs in".
            if (g.Any(n => n.Id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) && n.Class == "Bluetooth"))
                return null;

            string category = Categorize(g);
            if (category == null) return null;

            var btNode = g.FirstOrDefault(n => n.IsBtDeviceNode);
            string name = btNode?.Name;
            if (string.IsNullOrWhiteSpace(name))
                name = g.Where(n => n.Id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) && n.Id.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase) < 0)
                        .Select(n => n.BusName).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
                    ?? g.Select(n => n.BusName).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
                    ?? g.Select(n => n.Name).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
                    ?? key;

            bool isBt = g.Any(n => n.Id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)
                                 || n.Id.IndexOf("{00001124-", StringComparison.OrdinalIgnoreCase) >= 0
                                 || n.Id.IndexOf("{00001812-", StringComparison.OrdinalIgnoreCase) >= 0);

            // A paired Bluetooth device keeps its devnodes while switched off; only the radio
            // knows whether it's actually connected right now.
            bool connected = btNode == null || btNode.BtConnected == true;

            return new ExternalDevice
            {
                Key = key,
                Name = name.Trim(),
                Category = category,
                Bus = isBt ? "Bluetooth" : "USB",
                IsConnected = connected,
            };
        }

        // null = not a device worth listing (USB hubs, bare controllers).
        private static string Categorize(List<Node> g)
        {
            bool HasHw(string s) => g.Any(n => n.HwIds.Any(h => h.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0));
            bool HasCompat(string s) => g.Any(n => n.CompatIds.Any(h => h.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0));
            bool HasClass(params string[] cs) => g.Any(n => cs.Any(c => string.Equals(n.Class, c, StringComparison.OrdinalIgnoreCase)));

            // HID game pad / joystick usage, plus Xbox controllers, which use XUSB/GIP instead of HID.
            if (HasHw("UP:0001_U:0005") || HasHw("UP:0001_U:0004")
                || HasCompat(@"USB\Class_FF&SubClass_5D") || HasCompat(@"USB\MS_COMP_XGIP")
                || HasClass("XnaComposite", "XboxComposite"))
                return "Gamepad";

            bool kbd = HasClass("Keyboard"), mouse = HasClass("Mouse");
            if (kbd && mouse) return "Keyboard + Mouse";
            if (kbd) return "Keyboard";
            if (mouse) return "Mouse";
            if (HasClass("MEDIA", "AudioEndpoint")) return "Audio";
            if (HasClass("Camera", "Image")) return "Camera";
            if (HasClass("DiskDrive", "WPD", "Volume")) return "Storage";
            // Hubs: USB\Class_09 under the classic driver, USB\USB20_HUB / USB\USB30_HUB under USBHUB3.
            if (HasCompat(@"USB\Class_09") || HasCompat(@"USB\USB20_HUB") || HasCompat(@"USB\USB30_HUB")) return null;
            return "Other";
        }

        private static List<Node> Bucket(Dictionary<string, List<Node>> d, string key)
        {
            List<Node> l;
            if (!d.TryGetValue(key, out l)) d[key] = l = new List<Node>();
            return l;
        }

        // ---------------------------------------------------------------------
        // cfgmgr32 interop
        // ---------------------------------------------------------------------

        private const int CR_SUCCESS = 0;
        private const int CR_BUFFER_SMALL = 0x1A;
        private const int CM_GETIDLIST_FILTER_PRESENT = 0x100;

        private const int DEVPROP_TYPE_GUID = 0x0D;
        private const int DEVPROP_TYPE_BOOLEAN = 0x11;
        private const int DEVPROP_TYPE_STRING = 0x12;
        private const int DEVPROP_TYPE_STRING_LIST = 0x2012;

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;
            public DEVPROPKEY(string guid, uint pid) { fmtid = new Guid(guid); this.pid = pid; }
        }

        private static readonly DEVPROPKEY DeviceDescKey = new DEVPROPKEY("a45c254e-df1c-4efd-8020-67d146a850e0", 2);
        private static readonly DEVPROPKEY HardwareIdsKey = new DEVPROPKEY("a45c254e-df1c-4efd-8020-67d146a850e0", 3);
        private static readonly DEVPROPKEY CompatibleIdsKey = new DEVPROPKEY("a45c254e-df1c-4efd-8020-67d146a850e0", 4);
        private static readonly DEVPROPKEY ClassKey = new DEVPROPKEY("a45c254e-df1c-4efd-8020-67d146a850e0", 9);
        private static readonly DEVPROPKEY FriendlyNameKey = new DEVPROPKEY("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
        private static readonly DEVPROPKEY BusReportedDescKey = new DEVPROPKEY("540b947e-8b40-45bc-a8a2-6a0b894cbda2", 4);
        private static readonly DEVPROPKEY ContainerIdKey = new DEVPROPKEY("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c", 2);
        private static readonly DEVPROPKEY BtIsConnectedKey = new DEVPROPKEY("83da6326-97a6-4088-9453-a1923f573b29", 15);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_ID_List_SizeW(out int pulLen, string pszFilter, int ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_ID_ListW(string pszFilter, char[] buffer, int bufferLen, int ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Locate_DevNodeW(out int pdnDevInst, string pDeviceID, int ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_DevNode_PropertyW(int dnDevInst, ref DEVPROPKEY propertyKey,
            out int propertyType, byte[] propertyBuffer, ref int propertyBufferSize, int ulFlags);

        private static IEnumerable<string> GetPresentDeviceIds()
        {
            // The list can grow between the size query and the fetch (something got plugged in) — retry.
            for (int attempt = 0; attempt < 5; attempt++)
            {
                int len;
                int cr = CM_Get_Device_ID_List_SizeW(out len, null, CM_GETIDLIST_FILTER_PRESENT);
                if (cr != CR_SUCCESS) throw new InvalidOperationException("CM_Get_Device_ID_List_Size failed: 0x" + cr.ToString("X"));
                var buf = new char[len];
                cr = CM_Get_Device_ID_ListW(null, buf, len, CM_GETIDLIST_FILTER_PRESENT);
                if (cr == CR_BUFFER_SMALL) continue;
                if (cr != CR_SUCCESS) throw new InvalidOperationException("CM_Get_Device_ID_List failed: 0x" + cr.ToString("X"));
                return SplitMultiSz(buf, buf.Length);
            }
            throw new InvalidOperationException("Device list kept changing while reading it.");
        }

        private static byte[] GetRaw(int devInst, DEVPROPKEY key, out int type)
        {
            int size = 0;
            int cr = CM_Get_DevNode_PropertyW(devInst, ref key, out type, null, ref size, 0);
            if (cr != CR_BUFFER_SMALL || size == 0) return null;
            var buf = new byte[size];
            cr = CM_Get_DevNode_PropertyW(devInst, ref key, out type, buf, ref size, 0);
            return cr == CR_SUCCESS ? buf : null;
        }

        private static string GetString(int devInst, DEVPROPKEY key)
        {
            int type;
            var b = GetRaw(devInst, key, out type);
            if (b == null || type != DEVPROP_TYPE_STRING) return null;
            return Encoding.Unicode.GetString(b).TrimEnd('\0');
        }

        private static string[] GetStringList(int devInst, DEVPROPKEY key)
        {
            int type;
            var b = GetRaw(devInst, key, out type);
            if (b == null || type != DEVPROP_TYPE_STRING_LIST) return Array.Empty<string>();
            var chars = Encoding.Unicode.GetChars(b);
            return SplitMultiSz(chars, chars.Length).ToArray();
        }

        private static Guid GetGuid(int devInst, DEVPROPKEY key)
        {
            int type;
            var b = GetRaw(devInst, key, out type);
            return b != null && type == DEVPROP_TYPE_GUID && b.Length == 16 ? new Guid(b) : Guid.Empty;
        }

        private static bool? GetBool(int devInst, DEVPROPKEY key)
        {
            int type;
            var b = GetRaw(devInst, key, out type);
            if (b == null || type != DEVPROP_TYPE_BOOLEAN || b.Length < 1) return null;
            return b[0] != 0;
        }

        private static List<string> SplitMultiSz(char[] buf, int len)
        {
            var list = new List<string>();
            int start = 0;
            for (int i = 0; i < len; i++)
            {
                if (buf[i] != '\0') continue;
                if (i == start) break;   // empty string = end of list
                list.Add(new string(buf, start, i - start));
                start = i + 1;
            }
            return list;
        }
    }

}
