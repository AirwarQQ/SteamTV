// Finds Android TVs on the local network (ADB over TCP listens on 5555), learns the TV's MAC from
// the ARP cache, and wakes it with a Wake-on-LAN packet. Nothing here talks ADB, so scanning
// doesn't pop "Allow USB debugging?" on every Android device in the house.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace SteamTV
{
    internal sealed class FoundTv
    {
        public string Ip, Name, Mac;
        public override string ToString() => string.IsNullOrEmpty(Name) || Name == Ip ? Ip : Name + " (" + Ip + ")";
    }

    internal static class TvDiscovery
    {
        public const int AdbPort = 5555;

        // Every host on the PC's own /24 networks with the ADB port open.
        public static async Task<List<FoundTv>> ScanAsync()
        {
            var targets = new List<IPAddress>();
            foreach (var own in LocalIPv4())
            {
                var b = own.GetAddressBytes();
                for (int i = 1; i < 255; i++)
                    if (i != b[3]) targets.Add(new IPAddress(new[] { b[0], b[1], b[2], (byte)i }));
            }

            var hits = await Task.WhenAll(targets.Select(async ip => await IsPortOpenAsync(ip, AdbPort, 600) ? ip : null));
            var found = new List<FoundTv>();
            foreach (var ip in hits.Where(h => h != null))
                found.Add(new FoundTv { Ip = ip.ToString(), Name = await HostNameAsync(ip), Mac = MacOf(ip) });
            return found;
        }

        // After a DHCP change: the host on the network that has the TV's MAC.
        public static async Task<string> FindByMacAsync(string mac)
        {
            if (string.IsNullOrEmpty(mac)) return null;
            var tv = (await ScanAsync()).FirstOrDefault(t => string.Equals(t.Mac, mac, StringComparison.OrdinalIgnoreCase));
            return tv?.Ip;
        }

        public static async Task<bool> IsPortOpenAsync(IPAddress ip, int port, int timeoutMs)
        {
            using (var c = new TcpClient())
            {
                try
                {
                    var connect = c.ConnectAsync(ip, port);
                    if (await Task.WhenAny(connect, Task.Delay(timeoutMs)) != connect) return false;
                    await connect;
                    return c.Connected;
                }
                catch { return false; }
            }
        }

        private static async Task<string> HostNameAsync(IPAddress ip)
        {
            try
            {
                var lookup = Dns.GetHostEntryAsync(ip);
                if (await Task.WhenAny(lookup, Task.Delay(1500)) != lookup) return null;
                string name = (await lookup).HostName;
                int dot = name.IndexOf('.');
                return dot > 0 ? name.Substring(0, dot) : name;
            }
            catch { return null; }
        }

        private static IEnumerable<IPAddress> LocalIPv4() =>
            NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                            && n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address)
                .Distinct();

        // "aa-bb-cc-dd-ee-ff", via an ARP request (works for any host on the same subnet), or null.
        public static string MacOf(string ip)
        {
            IPAddress a;
            return IPAddress.TryParse(ip, out a) ? MacOf(a) : null;
        }

        private static string MacOf(IPAddress ip)
        {
            try
            {
                var mac = new byte[6];
                int len = mac.Length;
                if (SendARP(BitConverter.ToInt32(ip.GetAddressBytes(), 0), 0, mac, ref len) != 0 || len != 6) return null;
                return string.Join("-", mac.Select(x => x.ToString("x2")));
            }
            catch { return null; }
        }

        // Magic packet to the broadcast address of every local network. Many Android TVs only
        // honour it with "Wake on LAN / network standby" on, and some only over Ethernet.
        public static void SendWakeOnLan(string mac)
        {
            var bytes = (mac ?? "").Split('-', ':').Where(p => p.Length == 2).Select(p => Convert.ToByte(p, 16)).ToArray();
            if (bytes.Length != 6) return;
            var packet = Enumerable.Repeat((byte)0xFF, 6).Concat(Enumerable.Repeat(bytes, 16).SelectMany(x => x)).ToArray();
            using (var udp = new UdpClient { EnableBroadcast = true })
            {
                var targets = new List<IPAddress> { IPAddress.Broadcast };
                targets.AddRange(LocalIPv4().Select(ip => { var b = ip.GetAddressBytes(); b[3] = 255; return new IPAddress(b); }));
                foreach (var t in targets.Distinct())
                    foreach (int port in new[] { 9, 7 })
                        try { udp.Send(packet, packet.Length, new IPEndPoint(t, port)); } catch { }
            }
        }

        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern int SendARP(int destIp, int srcIp, byte[] macAddr, ref int physicalAddrLen);
    }
}
