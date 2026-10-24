// Optional Home Assistant link over MQTT: announces one "SteamTV" device with two switches via
// MQTT discovery, publishes their state and turns commands from HA into Command events.
// Nothing here runs unless the integration is switched on in settings.
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace SteamTV
{

    internal enum HaSwitch { Monitoring, SteamOnTv }

    internal sealed class MqttConfig
    {
        public string Host, User, Password, DeviceId;
        public int Port = 1883;

        public static MqttConfig From(AppSettings s) => new MqttConfig
        {
            Host = (s.MqttHost ?? "").Trim(), Port = s.MqttPort, User = s.MqttUser, Password = s.MqttPassword,
            DeviceId = s.MqttDeviceId,
        };
    }

    internal sealed class HomeAssistantMqtt
    {
        private const string DiscoveryPrefix = "homeassistant";
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

        private readonly MonitorService _monitor;
        private readonly string _version;
        private readonly MqttFactory _factory = new MqttFactory();

        private CancellationTokenSource _cts;
        private Task _loop;
        private MqttConfig _cfg;
        private volatile IMqttClient _client;   // non-null only while connected and announced

        public event Action<string> Log;
        public event Action<string> StatusChanged;
        // Raised on a thread-pool thread — the receiver marshals to the UI thread itself.
        public event Action<HaSwitch, bool> Command;

        public HomeAssistantMqtt(MonitorService monitor, string version)
        {
            _monitor = monitor;
            _version = version;
            _monitor.RunningChanged += on => PublishState(HaSwitch.Monitoring, on);
            _monitor.DisplayActiveChanged += on => PublishState(HaSwitch.SteamOnTv, on);
        }

        public bool IsStarted => _cts != null;

        public void Start(MqttConfig cfg)
        {
            Stop();
            _cfg = cfg;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _loop = Task.Run(() => RunAsync(cfg, ct));
        }

        // Says goodbye properly (availability = offline) and waits briefly for that to go out.
        public void Stop()
        {
            if (_cts == null) return;
            _cts.Cancel();
            try { _loop?.Wait(3000); } catch { }
            _cts.Dispose();
            _cts = null;
            _loop = null;
            Status("Off");
        }

        // One-shot connect with the given settings (under a separate client id, so it never kicks
        // the live connection off the broker). Returns null on success, otherwise the reason.
        public static async Task<string> TestAsync(MqttConfig cfg)
        {
            var client = new MqttFactory().CreateMqttClient();
            try
            {
                using (var cts = new CancellationTokenSource(ConnectTimeout))
                {
                    var result = await client.ConnectAsync(BuildOptions(cfg, ClientId(cfg) + "-test", withWill: false), cts.Token);
                    if (result.ResultCode != MqttClientConnectResultCode.Success)
                        return result.ResultCode + (string.IsNullOrEmpty(result.ReasonString) ? "" : " — " + result.ReasonString);
                    await client.DisconnectAsync();
                    return null;
                }
            }
            catch (OperationCanceledException) { return "timed out after " + ConnectTimeout.TotalSeconds + " s"; }
            catch (Exception ex) { return Describe(ex); }
            finally { client.Dispose(); }
        }

        // ---------------------------------------------------------------------

        private async Task RunAsync(MqttConfig cfg, CancellationToken ct)
        {
            int delaySec = 5;
            bool failureLogged = false;
            string where = cfg.Host + ":" + cfg.Port;

            while (!ct.IsCancellationRequested)
            {
                var client = _factory.CreateMqttClient();
                var lost = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                client.DisconnectedAsync += e => { lost.TrySetResult(true); return Task.CompletedTask; };
                client.ApplicationMessageReceivedAsync += e => { OnMessage(e.ApplicationMessage); return Task.CompletedTask; };
                try
                {
                    Status("Connecting to " + where + "…");
                    using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        connectCts.CancelAfter(ConnectTimeout);
                        var result = await client.ConnectAsync(BuildOptions(cfg, ClientId(cfg), withWill: true), connectCts.Token);
                        if (result.ResultCode != MqttClientConnectResultCode.Success)
                            throw new InvalidOperationException("broker refused: " + result.ResultCode);
                    }

                    await client.SubscribeAsync(_factory.CreateSubscribeOptionsBuilder()
                        .WithTopicFilter(f => f.WithTopic(Base + "/+/set").WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                        .WithTopicFilter(f => f.WithTopic(DiscoveryPrefix + "/status"))
                        .Build(), ct);
                    await AnnounceAsync(client, ct);
                    _client = client;

                    L("Home Assistant: connected to MQTT broker " + where + ".");
                    Status("Connected to " + where);
                    delaySec = 5;
                    failureLogged = false;

                    using (ct.Register(() => lost.TrySetResult(false)))
                        await lost.Task;
                    _client = null;

                    if (ct.IsCancellationRequested)
                    {
                        await SayGoodbyeAsync(client, cfg);
                        L("Home Assistant: disconnected from MQTT broker.");
                        break;
                    }
                    L("Home Assistant: connection to " + where + " lost — reconnecting.");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    _client = null;
                    break;
                }
                catch (Exception ex)
                {
                    _client = null;
                    string why = ex is OperationCanceledException ? "timed out" : Describe(ex);
                    Status("Not connected: " + why + " (retrying)");
                    if (!failureLogged)
                    {
                        L("Home Assistant: can't connect to MQTT broker " + where + " — " + why + ". Will keep retrying.");
                        failureLogged = true;
                    }
                }
                finally
                {
                    client.Dispose();
                }

                try { await Task.Delay(TimeSpan.FromSeconds(delaySec), ct); }
                catch (OperationCanceledException) { break; }
                delaySec = Math.Min(60, delaySec * 2);
            }
        }

        private static async Task SayGoodbyeAsync(IMqttClient client, MqttConfig cfg)
        {
            // The will only fires on an unclean drop, so mark ourselves offline by hand on a clean exit.
            try
            {
                using (var cts = new CancellationTokenSource(2000))
                {
                    if (!client.IsConnected) return;
                    await client.PublishAsync(Message("steamtv/" + cfg.DeviceId + "/availability", "offline", retain: true), cts.Token);
                    await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), cts.Token);
                }
            }
            catch { }
        }

        // Discovery configs + availability + current states. Also re-run when HA itself comes back
        // online (homeassistant/status = online), so nothing depends on the broker keeping retained data.
        private async Task AnnounceAsync(IMqttClient client, CancellationToken ct)
        {
            await client.PublishAsync(Message(Base + "/availability", "online", retain: true), ct);
            await client.PublishAsync(Message(DiscoveryTopic(HaSwitch.Monitoring), DiscoveryJson(HaSwitch.Monitoring), retain: true), ct);
            await client.PublishAsync(Message(DiscoveryTopic(HaSwitch.SteamOnTv), DiscoveryJson(HaSwitch.SteamOnTv), retain: true), ct);
            await client.PublishAsync(Message(StateTopic(HaSwitch.Monitoring), OnOff(_monitor.IsRunning), retain: true), ct);
            await client.PublishAsync(Message(StateTopic(HaSwitch.SteamOnTv), OnOff(_monitor.IsDisplayActive), retain: true), ct);
        }

        private void OnMessage(MqttApplicationMessage m)
        {
            string topic = m.Topic ?? "";
            string payload = (m.ConvertPayloadToString() ?? "").Trim();

            if (topic == DiscoveryPrefix + "/status")
            {
                var client = _client;
                if (client != null && payload == "online")
                    _ = Task.Run(async () => { try { await AnnounceAsync(client, CancellationToken.None); } catch { } });
                return;
            }

            HaSwitch sw;
            if (topic == SetTopic(HaSwitch.Monitoring)) sw = HaSwitch.Monitoring;
            else if (topic == SetTopic(HaSwitch.SteamOnTv)) sw = HaSwitch.SteamOnTv;
            else return;

            bool on;
            if (string.Equals(payload, "ON", StringComparison.OrdinalIgnoreCase)) on = true;
            else if (string.Equals(payload, "OFF", StringComparison.OrdinalIgnoreCase)) on = false;
            else { L("Home Assistant: ignored unknown command '" + payload + "' for " + Title(sw) + "."); return; }

            L("Home Assistant command: " + Title(sw) + " → " + (on ? "ON" : "OFF"));
            Command?.Invoke(sw, on);
            // Commands can be no-ops (already in that state, or refused) — echo the real state back
            // so the toggle in HA doesn't hang on a value that never arrives. Delayed, so a command that
            // does take effect has already published its new state and the toggle doesn't flicker.
            _ = Task.Delay(1500).ContinueWith(_ =>
                PublishState(sw, sw == HaSwitch.Monitoring ? _monitor.IsRunning : _monitor.IsDisplayActive));
        }

        private void PublishState(HaSwitch sw, bool on)
        {
            var client = _client;
            if (client == null) return;
            _ = Task.Run(async () =>
            {
                try { await client.PublishAsync(Message(StateTopic(sw), OnOff(on), retain: true)); }
                catch { }   // connection dropped mid-publish — the reconnect re-announces current state anyway
            });
        }

        // ---------------------------------------------------------------------
        // Topics and payloads
        // ---------------------------------------------------------------------

        private string Base => "steamtv/" + _cfg.DeviceId;
        private static string Key(HaSwitch sw) => sw == HaSwitch.Monitoring ? "monitoring" : "steam_on_tv";
        private static string Title(HaSwitch sw) => sw == HaSwitch.Monitoring ? "Gamepad monitoring" : "Steam on TV";
        private string StateTopic(HaSwitch sw) => Base + "/" + Key(sw) + "/state";
        private string SetTopic(HaSwitch sw) => Base + "/" + Key(sw) + "/set";
        private string DiscoveryTopic(HaSwitch sw) => DiscoveryPrefix + "/switch/steamtv_" + _cfg.DeviceId + "/" + Key(sw) + "/config";
        private static string ClientId(MqttConfig c) => "steamtv-" + c.DeviceId;
        private static string OnOff(bool on) => on ? "ON" : "OFF";

        private string DiscoveryJson(HaSwitch sw)
        {
            string id = "steamtv_" + _cfg.DeviceId;
            return "{"
                + Prop("name", Title(sw)) + ","
                + Prop("unique_id", id + "_" + Key(sw)) + ","
                + Prop("icon", sw == HaSwitch.Monitoring ? "mdi:controller" : "mdi:television-play") + ","
                + Prop("command_topic", SetTopic(sw)) + ","
                + Prop("state_topic", StateTopic(sw)) + ","
                + Prop("availability_topic", Base + "/availability") + ","
                + "\"device\":{"
                    + "\"identifiers\":[" + Str(id) + "],"
                    + Prop("name", "SteamTV") + ","
                    + Prop("manufacturer", "SteamTV") + ","
                    + Prop("model", "Windows tray app (" + Environment.MachineName + ")") + ","
                    + Prop("sw_version", _version)
                + "}}";
        }

        private static string Prop(string name, string value) => Str(name) + ":" + Str(value);

        private static string Str(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        private static MqttApplicationMessage Message(string topic, string payload, bool retain) =>
            new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithRetainFlag(retain)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

        private static MqttClientOptions BuildOptions(MqttConfig c, string clientId, bool withWill)
        {
            var b = new MqttClientOptionsBuilder()
                .WithTcpServer(c.Host, c.Port)
                .WithClientId(clientId)
                .WithCleanSession()
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
                .WithTimeout(ConnectTimeout);
            if (!string.IsNullOrEmpty(c.User))
                b = b.WithCredentials(c.User, c.Password ?? "");
            if (withWill)
                b = b.WithWillTopic("steamtv/" + c.DeviceId + "/availability")
                     .WithWillPayload("offline")
                     .WithWillRetain(true)
                     .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
            return b.Build();
        }

        // MQTTnet wraps socket errors a couple of levels deep; the innermost message is the useful one.
        private static string Describe(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex.Message;
        }

        private void L(string msg) => Log?.Invoke(msg);
        private void Status(string s) => StatusChanged?.Invoke(s);
    }

}
