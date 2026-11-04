// "Set up automatically": find the TV, connect, and fill in everything that can be read from the
// TV and from Windows — so a fresh install needs no ADB knowledge. Runs by itself on first launch.
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace SteamTV
{
    public partial class MainWindow
    {
        private bool _setupRunning;

        private async void AutoSetup_Click(object sender, RoutedEventArgs e) => await RunAutoSetupAsync(firstRun: false);

        private async void FindTv_Click(object sender, RoutedEventArgs e)
        {
            BtnFindTv.IsEnabled = false;
            try { await FindTvsAsync(); }
            finally { BtnFindTv.IsEnabled = !_monitor.IsRunning; }
        }

        private void CmbFoundTvs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbFoundTvs.SelectedItem is FoundTv tv) TxtIp.Text = tv.Ip;
        }

        private void DetectDisplay_Click(object sender, RoutedEventArgs e)
        {
            var tv = DisplayManager.FindTvDisplay();
            if (tv == null) { Setup("No display named like a TV — pick its number from Displays…"); return; }
            NumTarget.Value = tv.Value.Number;
            SaveUiToSettings();
            Setup("TV display: #" + tv.Value.Number + " (" + tv.Value.FriendlyName + ").");
        }

        private void Setup(string message)
        {
            LblSetup.Text = message;
            AppendLog("Setup: " + message);
        }

        // Fills the combo with the TVs found; returns them.
        private async Task<FoundTv[]> FindTvsAsync()
        {
            Setup("Looking for Android TVs on the network…");
            var found = (await TvDiscovery.ScanAsync()).ToArray();
            CmbFoundTvs.ItemsSource = found;
            CmbFoundTvs.Visibility = found.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
            if (found.Length == 0)
                Setup("No Android TV found. On the TV: Developer options → turn on USB debugging / network debugging, and make sure it's on the same network.");
            else if (found.Length == 1) { TxtIp.Text = found[0].Ip; Setup("Found " + found[0] + "."); }
            else Setup("Found " + found.Length + " devices — pick the TV in the list next to Find TV.");
            return found;
        }

        private async Task RunAutoSetupAsync(bool firstRun)
        {
            if (_setupRunning || _monitor.IsRunning) return;
            _setupRunning = true;
            BtnAutoSetup.IsEnabled = BtnFindTv.IsEnabled = false;
            try
            {
                // 1. The TV's address: keep the entered one if ADB answers there, else search.
                string ip = TxtIp.Text.Trim();
                bool reachable = System.Net.IPAddress.TryParse(ip, out _) &&
                                 await TvDiscovery.IsPortOpenAsync(System.Net.IPAddress.Parse(ip), TvDiscovery.AdbPort, 1500);
                if (!reachable)
                {
                    var found = await FindTvsAsync();
                    if (found.Length != 1) return;
                    ip = found[0].Ip;
                }

                // 2. Connect and get past the TV's "Allow USB debugging?" prompt.
                var adb = new Adb(AdbPathFromUi(), ip);
                Setup("Connecting to " + ip + "…");
                string state = await Task.Run(() => adb.GetState());
                for (int i = 0; state != "device" && i < 20; i++)
                {
                    Setup("On the TV, accept \"Allow USB debugging?\" (tick \"Always allow\")… " + (60 - i * 3) + " s");
                    await Task.Delay(3000);
                    state = await Task.Run(() => adb.GetState());
                }
                if (state != "device")
                {
                    Setup("The TV didn't allow the connection (" + (state ?? "no answer") + "). Accept the prompt on the TV, then click Set up automatically again.");
                    return;
                }

                // 3. Everything that can be read from the TV.
                string model = await Task.Run(() => adb.GetModel());
                _settings.TvMac = TvDiscovery.MacOf(ip) ?? _settings.TvMac;
                string launcher = await Task.Run(() => { try { return adb.DetectHomeComponent(); } catch { return ""; } });
                var ports = await Task.Run(() => adb.ConnectedHdmiPorts());

                TxtIp.Text = ip;
                TxtTvHomeComponent.Text = launcher;
                int port = (int)(NumHdmiPort.Value ?? 1);
                string portNote;
                if (ports.Count == 1) { port = ports[0]; portNote = "HDMI " + port; }
                else if (ports.Count == 0) portNote = "HDMI " + port + " (the TV reports no connected input — check it)";
                else
                {
                    if (!ports.Contains(port)) port = ports[0];
                    portNote = "HDMI " + port + " (cables in " + string.Join(", ", ports) + " — change it if the PC is on another)";
                }
                NumHdmiPort.Value = port;

                // 4. ...and from Windows.
                var display = DisplayManager.FindTvDisplay();
                if (display != null) NumTarget.Value = display.Value.Number;
                string steam = SteamHelper.FindSteamExe();
                if (steam != null) TxtSteamPath.Text = steam;
                SaveUiToSettings();

                Setup("Done: " + (string.IsNullOrEmpty(model) ? "TV" : model) + " at " + ip + " · " + portNote +
                      " · display " + (display != null ? "#" + display.Value.Number + " (" + display.Value.FriendlyName + ")" : "not found — pick it from Displays…") +
                      " · launcher " + (launcher.Length > 0 ? launcher.Split('/')[0] : "Home key") +
                      " · Steam " + (steam != null ? "found" : "not found — set its path on Advanced") +
                      ". Next: tick your gamepad on the Devices tab, then Start.");
                if (firstRun) Tabs.SelectedIndex = 0;
            }
            catch (Exception ex) { Setup("Setup failed: " + ex.Message); }
            finally
            {
                _setupRunning = false;
                BtnAutoSetup.IsEnabled = BtnFindTv.IsEnabled = !_monitor.IsRunning;
            }
        }

        private string AdbPathFromUi() => string.IsNullOrWhiteSpace(TxtAdbPath.Text) ? "adb.exe" : TxtAdbPath.Text.Trim();
    }
}
