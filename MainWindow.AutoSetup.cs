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
            finally { BtnFindTv.IsEnabled = true; }
        }

        private void CmbFoundTvs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbFoundTvs.SelectedItem is FoundTv tv) { TxtIp.Text = tv.Ip; SaveUiToSettings(); }
        }

        private void DetectDisplay_Click(object sender, RoutedEventArgs e)
        {
            var tv = DisplayManager.FindTvDisplay();
            if (tv == null) { Setup(Loc.T("Setup.NoTvDisplay")); return; }
            FillDisplays(tv.Value.Number);
            SaveUiToSettings();
            Setup(Loc.T("Setup.TvDisplay", tv.Value.FriendlyName + " · #" + tv.Value.Number));
        }

        // Progress goes to Settings → TV and, while the TV isn't set up yet, under the Home button too.
        private void Setup(string message)
        {
            LblSetup.Text = LblHomeSetup.Text = message;
            LblSetup.Visibility = Visibility.Visible;
            LblHomeSetup.Visibility = TvConfigured && !_setupRunning ? Visibility.Collapsed : Visibility.Visible;
            AppendLog("Setup: " + message);
        }

        // Fills the list with the TVs found; returns them.
        private async Task<FoundTv[]> FindTvsAsync()
        {
            Setup(Loc.T("Setup.Looking"));
            var found = (await TvDiscovery.ScanAsync()).ToArray();
            CmbFoundTvs.ItemsSource = found;
            CmbFoundTvs.Visibility = found.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
            if (found.Length == 0) Setup(Loc.T("Setup.NoneFound"));
            else if (found.Length == 1) { TxtIp.Text = found[0].Ip; Setup(Loc.T("Setup.FoundOne", found[0])); }
            else
            {
                Setup(Loc.T("Setup.FoundMany", found.Length));
                GoTo(2);   // the list is on the Settings page
            }
            return found;
        }

        private async Task RunAutoSetupAsync(bool firstRun)
        {
            if (_setupRunning) return;
            _setupRunning = true;
            BtnAutoSetup.IsEnabled = BtnFindTv.IsEnabled = BtnHomeSetup.IsEnabled = false;
            try
            {
                // 1. The TV's address: keep the entered one if ADB answers there, else search.
                string ip = TxtIp.Text.Trim();
                bool reachable = System.Net.IPAddress.TryParse(ip, out var parsed) &&
                                 await TvDiscovery.IsPortOpenAsync(parsed, TvDiscovery.AdbPort, 1500);
                if (!reachable)
                {
                    var found = await FindTvsAsync();
                    if (found.Length != 1) return;
                    ip = found[0].Ip;
                }

                // 2. Connect and get past the TV's "Allow USB debugging?" prompt.
                var adb = new Adb(AdbPathFromUi(), ip);
                Setup(Loc.T("Setup.Connecting", ip));
                string state = await Task.Run(() => adb.GetState());
                for (int i = 0; state != "device" && i < 20; i++)
                {
                    Setup(Loc.T("Setup.Accept", 60 - i * 3));
                    await Task.Delay(3000);
                    state = await Task.Run(() => adb.GetState());
                }
                if (state != "device")
                {
                    Setup(Loc.T("Setup.Denied", state ?? Loc.T("Setup.NoAnswer")));
                    return;
                }

                // 3. Everything that can be read from the TV.
                string model = await Task.Run(() => adb.GetModel());
                _settings.TvMac = TvDiscovery.MacOf(ip) ?? _settings.TvMac;
                string launcher = await Task.Run(() => { try { return adb.DetectHomeComponent(); } catch { return ""; } });
                var ports = await Task.Run(() => adb.ConnectedHdmiPorts());

                TxtIp.Text = ip;
                TxtTvHomeComponent.Text = launcher;
                int port = HdmiPortUi;
                string portNote;
                if (ports.Count == 1) { port = ports[0]; portNote = Loc.T("Setup.Port.One", port); }
                else if (ports.Count == 0) portNote = Loc.T("Setup.Port.None", port);
                else
                {
                    if (!ports.Contains(port)) port = ports[0];
                    portNote = Loc.T("Setup.Port.Many", port, string.Join(", ", ports));
                }
                HdmiPortUi = port;

                // 4. ...and from Windows.
                var display = DisplayManager.FindTvDisplay();
                if (display != null) FillDisplays(display.Value.Number);
                string steam = SteamHelper.FindSteamExe();
                if (steam != null) TxtSteamPath.Text = steam;
                _setupRunning = false;
                SaveUiToSettings();

                Setup(Loc.T("Setup.Done", string.IsNullOrEmpty(model) ? "TV" : model, ip, portNote,
                    display != null ? display.Value.FriendlyName + " · #" + display.Value.Number : Loc.T("Setup.Display.Missing"),
                    launcher.Length > 0 ? launcher.Split('/')[0] : Loc.T("Setup.HomeKey"),
                    Loc.T(steam != null ? "Setup.Steam.Found" : "Setup.Steam.Missing")));
                LblHomeSetup.Visibility = Visibility.Collapsed;
                if (firstRun && _settings.WatchedDevices.Count == 0) GoTo(1);   // next step: pick the controller
            }
            catch (Exception ex) { Setup(Loc.T("Setup.Failed", ex.Message)); }
            finally
            {
                _setupRunning = false;
                BtnAutoSetup.IsEnabled = BtnFindTv.IsEnabled = BtnHomeSetup.IsEnabled = true;
                UpdateStatusLabel();
            }
        }

        private string AdbPathFromUi() => string.IsNullOrWhiteSpace(TxtAdbPath.Text) ? "adb.exe" : TxtAdbPath.Text.Trim();
    }
}
