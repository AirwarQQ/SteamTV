// Main window: settings sync, Test tab, status, log, window events. Tabs with more code live in MainWindow.*.cs.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Forms = System.Windows.Forms;

namespace SteamTV
{

    public partial class MainWindow
    {
        private readonly AppSettings _settings;
        private readonly MonitorService _monitor;
        private readonly HomeAssistantMqtt _ha;

        private Forms.NotifyIcon _tray;
        private Forms.ToolStripMenuItem _miTrayAutostart;
        private Forms.ToolStripMenuItem _miTrayAutoMonitor;
        private Forms.ToolStripMenuItem _miTrayAdbStatus;
        private Forms.ToolStripMenuItem _miTrayMonitorStatus;
        private System.Drawing.Icon _trayIconUnknown;
        private System.Drawing.Icon _trayIconOk;
        private System.Drawing.Icon _trayIconBad;
        private DispatcherTimer _statusTimer;
        private bool _reallyExit;

        // ADB periodic check state
        private DateTime _lastAdbCheck = DateTime.MinValue;
        private bool _adbCheckPending;
        private bool? _lastAdbReachable;
        private int _adbFailStreak;
        private bool _adbFailNotified;

        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "SteamTV_Controller";

        public MainWindow(bool autostart)
        {
            InitializeComponent();

            Title = "SteamTV";

            _settings = AppSettings.Load();
            Loc.Set(_settings.Language);
            Loc.Changed += OnLanguageChanged;
            _monitor = new MonitorService(_settings);
            _monitor.Log += AppendLog;
            _monitor.RunningChanged += OnRunningChanged;
            _monitor.Notify += ShowTrayBalloon;
            _monitor.DisplayActiveChanged += _ => Dispatcher.BeginInvoke(new Action(UpdateStatusLabel));
            _monitor.PhaseChanged += () => Dispatcher.BeginInvoke(new Action(UpdateStatusLabel));
            _monitor.TvAddressChanged += ip => Dispatcher.BeginInvoke(new Action(() =>
            {
                TxtIp.Text = ip;
                ShowTrayBalloon("SteamTV", Loc.T("Msg.NewIp", ip));
            }));
            _ha = new HomeAssistantMqtt(_monitor, GetVersionString());
            _ha.Log += AppendLog;
            _ha.StatusChanged += OnMqttStatus;
            _ha.Command += OnHaCommand;
            _ha.PowerCommand += OnHaPowerCommand;

            LoadSettingsToUi();
            WireAutoSave();
            UpdateHotkeyHints();
            InitDevicesTab();
            BuildTestButtons();
            BuildTray();
            NavList.SelectedIndex = 0;
            UpdateStatusLabel();
            if (_settings.MqttEnabled) StartMqtt();

            _ = CheckForUpdatesAsync();

            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _statusTimer.Tick += (s, e) => OnStatusTick();
            _statusTimer.Start();

            if (autostart)
            {
                // App.xaml.cs always calls Show() once, unconditionally — Close()/Closing behave
                // the same whether or not the window was ever visible, and relying on that keeps
                // things simple. But Show() paints the window at its normal centered bounds first;
                // HideToTray() used to run on Loaded, which only fires *after* that first paint,
                // so on autostart the window would flash on screen — centered, and not even fully
                // rendered yet — before vanishing into the tray. Starting minimized + out of the
                // taskbar means Show() never paints a normal window at all, so there's nothing to see.
                WindowState = WindowState.Minimized;
                ShowInTaskbar = false;
                Loaded += (s, e) => { HideToTray(); if (_settings.AutoMonitorOnStart) StartMonitor(interactive: false); };
            }
            else if (_settings.IsFirstRun)
                Loaded += async (s, e) => await RunAutoSetupAsync(firstRun: true);
            else if (_settings.AutoMonitorOnStart)
                Loaded += (s, e) => StartMonitor(interactive: false);
        }

        private static string GetVersionString()
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return "v" + v.Major + "." + v.Minor + "." + v.Build;
        }

        // -------------------------------------------------------------------------
        // Update check — one-shot on startup, silent unless a newer release exists.
        // -------------------------------------------------------------------------

        private bool _updateAvailable;
        private string _updateTag;
        private Forms.ToolStripMenuItem _miTrayUpdate;

        // On start, then once a day (OnStatusTick) — SteamTV can sit in the tray for weeks.
        private DateTime _lastUpdateCheck = DateTime.MinValue;

        // manual: asked for from Settings → About (answer shown there) or the tray (answer as a
        // notification, since the window may be hidden).
        private async Task CheckForUpdatesAsync(bool manual = false, bool fromTray = false)
        {
            _lastUpdateCheck = DateTime.Now;
            if (manual && !fromTray) { LblUpdateStatus.Text = Loc.T("Upd.Checking"); LblUpdateStatus.Visibility = Visibility.Visible; }
            string tag = await UpdateChecker.GetLatestTagAsync();
            var current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (!UpdateChecker.IsNewer(tag, current))
            {
                if (manual)
                    Dispatcher.Invoke(() =>
                    {
                        string time = DateTime.Now.ToString("HH:mm");
                        LblUpdateStatus.Text = Loc.T(tag == null ? "Upd.CheckedFail" : "Upd.Checked", time);
                        LblUpdateStatus.Foreground = tag == null ? Rose : Mint;
                        LblUpdateStatus.Visibility = Visibility.Visible;
                        if (fromTray)
                            _tray?.ShowBalloonTip(5000, "SteamTV", Loc.T(tag == null ? "Upd.Failed" : "Upd.Latest"),
                                tag == null ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
                    });
                return;
            }

            Dispatcher.Invoke(() =>
            {
                if (manual) { _updateTag = tag; InstallUpdate(); return; }
                if (_updateTag == tag) return;   // already announced this one
                _updateAvailable = true;
                _updateTag = tag;
                if (_miTrayUpdate != null) { _miTrayUpdate.Text = Loc.T("Tray.Update", tag); _miTrayUpdate.Visible = true; }
                AppendLog("Update available: " + tag + " (running v" + current.Major + "." + current.Minor + "." + current.Build + ") — " + UpdateChecker.ReleasesPageUrl);
                _tray?.ShowBalloonTip(8000, Loc.T("Msg.UpdateTitle"), Loc.T("Msg.UpdateText", tag), Forms.ToolTipIcon.Info);
            });
        }

        private bool _updating;

        private async void InstallUpdate()
        {
            if (_updating || _updateTag == null) return;
            if (System.Windows.MessageBox.Show(Loc.T("Msg.UpdateAsk", _updateTag),
                    "SteamTV", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            _updating = true;
            AppendLog("Downloading " + _updateTag + "…");
            var restoring = _monitor.IsDisplayActive ? _monitor.DeactivateNow() : Task.CompletedTask;
            string err = await Updater.InstallAsync(_updateTag, startHidden: !IsVisible);
            await restoring;   // never exit halfway through putting the monitors back
            _updating = false;
            if (err == null) { AppendLog("Update installed — restarting."); _reallyExit = true; Close(); return; }
            AppendLog("Update failed: " + err);
            if (System.Windows.MessageBox.Show(Loc.T("Msg.UpdateFailed", err), "SteamTV",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo(UpdateChecker.ReleasesPageUrl) { UseShellExecute = true });
        }

        // -------------------------------------------------------------------------
        // Test tab — built at runtime (same as before, just re-targeted to WPF)
        // -------------------------------------------------------------------------

        private void BuildTestButtons()
        {
            TestButtonsPanel.Children.Add(TestBtn("Wake TV",
                () => new Adb(_settings.AdbPath, _settings.TvIp).WakeTv()));
            TestButtonsPanel.Children.Add(TestBtn("Switch HDMI Source", () =>
            {
                var adb = new Adb(_settings.AdbPath, _settings.TvIp);
                string method;
                adb.SwitchToHdmi(_settings.HdmiPort, out method);
                return "HDMI " + _settings.HdmiPort + " via " + method;
            }));

            TestButtonsPanel.Children.Add(TestBtn("Check ADB Connection", () =>
            {
                bool ok = new Adb(_settings.AdbPath, _settings.TvIp).IsAdbReachable(5000);
                return ok ? "reachable ✓" : "not reachable ✗";
            }));
            TestButtonsPanel.Children.Add(TestBtn("Restore TV Home Screen", () =>
            {
                new Adb(_settings.AdbPath, _settings.TvIp).RestoreSourceBeforeShutdown(_settings.TvHomeComponent);
                return "done";
            }));

            TestButtonsPanel.Children.Add(TestBtn("Start Big Picture", () =>
            {
                string err;
                return SteamHelper.StartBigPicture(_settings.SteamPath, out err) ? "launched" : "FAILED: " + err;
            }));
            TestButtonsPanel.Children.Add(TestBtn("List Displays", () =>
            {
                var list = DisplayManager.ListDisplays();
                if (list.Count == 0) return "none found";
                var sb = new StringBuilder();
                foreach (var d in list)
                    sb.Append("#" + d.Number + " " + (d.Active ? "[on]" : "[off]") + " " + d.FriendlyName + " | ");
                return sb.ToString().TrimEnd(' ', '|');
            }));

            TestButtonsPanel.Children.Add(TestBtn("Enable Display #N", () =>
            {
                string err;
                bool ok = DisplayManager.EnableDisplay(_settings.TargetDisplay, out err);
                return ok ? "enabled #" + _settings.TargetDisplay : err;
            }));
            TestButtonsPanel.Children.Add(TestBtn("Disable Display #N", () =>
            {
                string err;
                bool ok = DisplayManager.DisableDisplay(_settings.TargetDisplay, out err);
                return ok ? "disabled #" + _settings.TargetDisplay : err;
            }));

            TestButtonsPanel.Children.Add(TestBtn("Restore All Displays", () =>
            {
                string snapErr;
                var snap = DisplayManager.QueryAll(out snapErr);
                if (snap == null) return "query failed: " + snapErr;
                string err;
                bool ok = DisplayManager.RestoreAll(snap, out err);
                return ok ? "restored" : err;
            }));
            TestButtonsPanel.Children.Add(TestBtn("Minimize Steam", () =>
            {
                SteamHelper.MinimizeSteamWindow();
                return "done";
            }));

            TestButtonsPanel.Children.Add(TestBtn("Sleep TV", () =>
            {
                new Adb(_settings.AdbPath, _settings.TvIp).SleepTv();
                return "sent";
            }));
            TestButtonsPanel.Children.Add(TestBtn("TV: connected HDMI inputs", () =>
            {
                var adb = new Adb(_settings.AdbPath, _settings.TvIp);
                adb.Connect();
                var ports = adb.ConnectedHdmiPorts();
                return ports.Count == 0 ? "none reported" : "HDMI " + string.Join(", ", ports) + " · showing " + (adb.GetForegroundComponent() ?? "?");
            }));
            TestButtonsPanel.Children.Add(TestBtn("List Sound Devices", () =>
            {
                string current = AudioSwitcher.GetDefaultId();
                return string.Join(" | ", AudioSwitcher.ListPlayback().Select(d => (d.Id == current ? "[default] " : "") + d.Name));
            }));
            TestButtonsPanel.Children.Add(TestBtn("TV HDR State", () =>
            {
                bool? hdr = DisplayManager.GetHdr(_settings.TargetDisplay);
                return hdr == null ? "not supported / display off" : (hdr.Value ? "on" : "off");
            }));

            TestButtonsPanel.Children.Add(TestBtn("Set Highest Refresh Rate", () =>
            {
                string err;
                bool ok = DisplayManager.SetHighestRefreshRate(_settings.TargetDisplay, out err, AppendLog);
                return ok ? "applied to #" + _settings.TargetDisplay : err;
            }));
        }

        private Button TestBtn(string label, Func<string> action)
        {
            var btn = new Button { Content = label, Margin = new Thickness(4) };
            btn.Click += (s, e) =>
            {
                SaveUiToSettings();
                AppendLog("[Test] " + label + "...");
                Task.Run(() =>
                {
                    string result;
                    try { result = action(); }
                    catch (Exception ex) { result = "ERROR: " + ex.Message; }
                    AppendLog("[Test] " + label + " → " + result);
                });
            };
            return btn;
        }

        // -------------------------------------------------------------------------
        // Settings sync
        // -------------------------------------------------------------------------

        private bool _loadingUi;

        private void LoadSettingsToUi()
        {
            _loadingUi = true;
            TxtIp.Text = _settings.TvIp;
            HdmiPortUi = _settings.HdmiPort;
            FillDisplays(_settings.TargetDisplay);
            ChkAutoMonitor.IsChecked = _settings.AutoMonitorOnStart;
            ChkMinimizeSteam.IsChecked = _settings.MinimizeSteamAfter;
            ChkAnimate.IsChecked = _settings.AnimateBanner;
            Scene.Animate = _settings.AnimateBanner;
            ChkHotkeyToggle.IsChecked = _settings.HotkeyToggle;
            UpdateHotkeyFields();
            FillGrace();
            CmbLanguage.SelectedItem = CmbLanguage.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(i => (string)i.Tag == (_settings.Language ?? "")) ?? CmbLanguage.Items[0];
            LblNavVersion.Text = GetVersionString();
            LblVersion.Text = Loc.T("Set.Version", GetVersionString());
            RowSupport.Visibility = BtnNavSupport.Visibility = string.IsNullOrEmpty(Links.Support) ? Visibility.Collapsed : Visibility.Visible;
            ChkDisableOthers.IsChecked = _settings.DisableOthers;
            ChkWakeTV.IsChecked = _settings.EnableWakeTV;
            ChkSourceSwitch.IsChecked = _settings.EnableSourceSwitch;
            ChkBigPicture.IsChecked = _settings.EnableBigPicture;
            ChkSwitchAudio.IsChecked = _settings.SwitchAudio;
            ChkHdr.IsChecked = _settings.EnableHdr;
            ChkRestoreTvApp.IsChecked = _settings.RestoreTvApp;
            ChkSleepTv.IsChecked = _settings.SleepTvAfter;
            TxtTvAudioDevice.Text = _settings.TvAudioDevice;
            ChkAutostart.IsChecked = IsAutostartEnabled();
            TxtAdbPath.Text = _settings.AdbPath;
            TxtSteamPath.Text = _settings.SteamPath;
            TxtTvHomeComponent.Text = _settings.TvHomeComponent;
            TxtActivateHotkey.Text = _settings.ActivateHotkey;
            TxtDeactivateHotkey.Text = _settings.DeactivateHotkey;
            ChkMqttEnabled.IsChecked = _settings.MqttEnabled;
            ChkMqttPower.IsChecked = _settings.MqttPowerButtons;
            _loadingUi = false;
            UpdateMqttPowerInfo();
            TxtPcMac.Text = PowerControl.PrimaryMac() ?? Loc.T("Ha.MacMissing");
            TxtMqttHost.Text = _settings.MqttHost;
            NumMqttPort.Value = _settings.MqttPort;
            TxtMqttUser.Text = _settings.MqttUser;
            PwdMqtt.Password = _settings.MqttPassword;
        }

        private void SaveUiToSettings()
        {
            if (_loadingUi) return;
            _settings.TvIp = TxtIp.Text.Trim();
            _settings.HdmiPort = HdmiPortUi;
            _settings.TargetDisplay = TargetDisplayUi ?? _settings.TargetDisplay;
            _settings.AutoMonitorOnStart = ChkAutoMonitor.IsChecked == true;
            _settings.MinimizeSteamAfter = ChkMinimizeSteam.IsChecked == true;
            _settings.AnimateBanner = ChkAnimate.IsChecked == true;
            Scene.Animate = _settings.AnimateBanner;
            if (CmbGrace.SelectedItem is ComboBoxItem grace) _settings.RestoreGraceSec = (int)grace.Tag;
            _settings.DisableOthers = ChkDisableOthers.IsChecked == true;
            _settings.EnableWakeTV = ChkWakeTV.IsChecked == true;
            _settings.EnableSourceSwitch = ChkSourceSwitch.IsChecked == true;
            _settings.EnableBigPicture = ChkBigPicture.IsChecked == true;
            _settings.SwitchAudio = ChkSwitchAudio.IsChecked == true;
            _settings.EnableHdr = ChkHdr.IsChecked == true;
            _settings.RestoreTvApp = ChkRestoreTvApp.IsChecked == true;
            _settings.SleepTvAfter = ChkSleepTv.IsChecked == true;
            _settings.TvAudioDevice = TxtTvAudioDevice.Text.Trim();
            _settings.AdbPath = string.IsNullOrWhiteSpace(TxtAdbPath.Text) ? "adb.exe" : TxtAdbPath.Text.Trim();
            _settings.SteamPath = TxtSteamPath.Text.Trim();
            _settings.TvHomeComponent = TxtTvHomeComponent.Text.Trim();
            _settings.Save();
            UpdateStatusLabel();
        }

        // Settings apply as soon as they change — no Save button, and nothing is locked while
        // monitoring runs: the monitor reads them at the start of each action.
        private void WireAutoSave()
        {
            RoutedEventHandler save = (s, e) => SaveUiToSettings();
            foreach (var t in new[] { ChkWakeTV, ChkSourceSwitch, ChkRestoreTvApp, ChkSleepTv, ChkDisableOthers,
                                      ChkSwitchAudio, ChkHdr, ChkBigPicture, ChkAutoMonitor, ChkMinimizeSteam, ChkAnimate })
            { t.Checked += save; t.Unchecked += save; }
            foreach (var t in new[] { TxtIp, TxtTvHomeComponent, TxtTvAudioDevice, TxtSteamPath, TxtAdbPath })
                t.LostKeyboardFocus += (s, e) => SaveUiToSettings();
            CmbHdmi.SelectionChanged += (s, e) => SaveUiToSettings();
            CmbDisplay.SelectionChanged += (s, e) => SaveUiToSettings();
            CmbGrace.SelectionChanged += (s, e) => { SaveUiToSettings(); RefreshDeviceTexts(); };
        }

        private int HdmiPortUi
        {
            get => (CmbHdmi.SelectedItem as ComboBoxItem)?.Tag is string t ? int.Parse(t) : _settings.HdmiPort;
            set => CmbHdmi.SelectedIndex = Math.Max(1, Math.Min(4, value)) - 1;
        }

        private int? TargetDisplayUi => (CmbDisplay.SelectedItem as ComboBoxItem)?.Tag as int?;

        // Displays by name, not by number: "Mi TV" rather than "#3". Windows lists a monitor once per
        // possible source, so each name appears once (lowest number); the saved one is always kept.
        private void FillDisplays(int selected)
        {
            bool loading = _loadingUi;
            _loadingUi = true;
            CmbDisplay.Items.Clear();
            var seen = new HashSet<string>();
            foreach (var d in DisplayManager.ListDisplays())
            {
                bool named = !string.IsNullOrEmpty(d.FriendlyName);
                if (d.Number != selected && (!named || !seen.Add(d.FriendlyName))) continue;
                if (named) seen.Add(d.FriendlyName);
                CmbDisplay.Items.Add(new ComboBoxItem
                {
                    Content = (named ? d.FriendlyName : "Display") + "  ·  #" + d.Number + (d.Active ? "" : " (off)"),
                    Tag = d.Number,
                });
            }
            if (!CmbDisplay.Items.Cast<ComboBoxItem>().Any(i => (int)i.Tag == selected))
                CmbDisplay.Items.Add(new ComboBoxItem { Content = "#" + selected + " (not connected)", Tag = selected });
            CmbDisplay.SelectedItem = CmbDisplay.Items.Cast<ComboBoxItem>().First(i => (int)i.Tag == selected);
            _loadingUi = loading;
        }

        private void CmbDisplay_DropDownOpened(object sender, EventArgs e) => FillDisplays(TargetDisplayUi ?? _settings.TargetDisplay);

        private static readonly int[] GraceChoices = { 5, 10, 15, 20, 30, 45, 60, 90, 120 };

        private void FillGrace()
        {
            bool loading = _loadingUi;
            _loadingUi = true;
            CmbGrace.Items.Clear();
            foreach (int s in GraceChoices.Union(new[] { _settings.RestoreGraceSec }).OrderBy(x => x))
                CmbGrace.Items.Add(new ComboBoxItem { Content = Loc.T("Set.Seconds", s), Tag = s });
            CmbGrace.SelectedItem = CmbGrace.Items.Cast<ComboBoxItem>().First(i => (int)i.Tag == _settings.RestoreGraceSec);
            _loadingUi = loading;
        }

        // Shared by Start and the manual Activate/Deactivate buttons — all of them need a valid IP.
        // Remote (Home Assistant) commands pass interactive: false — nobody may be at the PC to
        // dismiss a message box, so the problem goes to the log and a tray balloon instead.
        private bool ValidateIpOrWarn(bool interactive = true)
        {
            string problem = null;
            System.Net.IPAddress addr;
            if (string.IsNullOrWhiteSpace(TxtIp.Text))
                problem = Loc.T("Msg.NoTv");
            else if (!System.Net.IPAddress.TryParse(TxtIp.Text.Trim(), out addr))
                problem = Loc.T("Msg.BadIp");
            if (problem == null) return true;

            if (interactive)
                System.Windows.MessageBox.Show(problem, "SteamTV", MessageBoxButton.OK, MessageBoxImage.Warning);
            else
            {
                AppendLog("Command ignored: " + problem);
                ShowTrayBalloon("SteamTV", Loc.T("Msg.Ignored", problem));
            }
            return false;
        }

        private void StartMonitor(bool interactive = true)
        {
            if (!ValidateIpOrWarn(interactive)) return;
            SaveUiToSettings();
            _monitor.Start();
        }

        private void ActivateNow(bool interactive = true)
        {
            if (!ValidateIpOrWarn(interactive)) return;
            SaveUiToSettings();
            _monitor.ActivateNow();
        }

        private void DeactivateNow(bool interactive = true)
        {
            if (!ValidateIpOrWarn(interactive)) return;
            SaveUiToSettings();
            _monitor.DeactivateNow();
        }

        // -------------------------------------------------------------------------
        // Status + ADB check
        // -------------------------------------------------------------------------

        private void OnRunningChanged(bool running)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => OnRunningChanged(running))); return; }
            _syncingMonitorToggle = true;
            TglMonitoring.IsChecked = running;
            _syncingMonitorToggle = false;
            UpdateStatusLabel();
        }

        private bool _syncingMonitorToggle;

        private void TglMonitoring_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingMonitorToggle) return;
            if (TglMonitoring.IsChecked == true) StartMonitor();
            else _monitor.Stop();
            // StartMonitor can refuse (no TV yet) — show what really happened.
            _syncingMonitorToggle = true;
            TglMonitoring.IsChecked = _monitor.IsRunning;
            _syncingMonitorToggle = false;
        }

        private void OnStatusTick()
        {
            UpdateStatusLabel();
            if ((DateTime.Now - _lastUpdateCheck).TotalHours >= 24) _ = CheckForUpdatesAsync();

            // Live device status — only while someone can actually see the Devices tab.
            if (IsVisible && (CurrentPage <= 1) && ++_scanTick % 2 == 0)
                ScanDevices();

            // ADB check: run every 15 s in a background task
            if (!_adbCheckPending && (DateTime.Now - _lastAdbCheck).TotalSeconds >= 15)
            {
                System.Net.IPAddress addr;
                if (!System.Net.IPAddress.TryParse(_settings.TvIp ?? "", out addr))
                {
                    _lastAdbCheck = DateTime.Now;
                    SetAdbReachable(null);
                }
                else
                {
                    _adbCheckPending = true;
                    string ip = _settings.TvIp;
                    string adbPath = _settings.AdbPath;
                    Task.Run(() =>
                    {
                        bool ok = new Adb(adbPath, ip).IsAdbReachable(4000);
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            _adbCheckPending = false;
                            _lastAdbCheck = DateTime.Now;
                            SetAdbReachable(ok);

                            // Only nag while actively monitoring — ADB being unreachable while
                            // stopped just means the TV is off, not a problem worth a toast.
                            // TVs sometimes turn Wireless debugging back off on their own (e.g. after
                            // a reboot), so after ~45s of failures, point at the actual fix.
                            if (ok || !_monitor.IsRunning) { _adbFailStreak = 0; _adbFailNotified = false; return; }
                            _adbFailStreak++;
                            if (_adbFailStreak == 3) _ = LookForMovedTvAsync();
                            if (_adbFailStreak >= 3 && !_adbFailNotified)
                            {
                                _adbFailNotified = true;
                                ShowTrayBalloon("SteamTV", Loc.T("Msg.AdbLost"));
                            }
                        }));
                    });
                }
            }
        }

        // ADB stopped answering: maybe the router gave the TV another address. Its MAC tells.
        private async Task LookForMovedTvAsync()
        {
            string ip = await TvDiscovery.FindByMacAsync(_settings.TvMac);
            if (ip == null || ip == _settings.TvIp) return;
            AppendLog("TV found at a new address: " + ip + " (was " + _settings.TvIp + ").");
            _settings.TvIp = ip;
            _settings.Save();
            TxtIp.Text = ip;
            _adbFailStreak = 0;
            _lastAdbCheck = DateTime.MinValue;   // re-check right away
            ShowTrayBalloon("SteamTV", Loc.T("Msg.NewIp", ip));
        }

        // -------------------------------------------------------------------------
        // Home page, sidebar and tray status — one place, refreshed every second
        // -------------------------------------------------------------------------

        private static readonly System.Windows.Media.Brush Sky = (System.Windows.Media.Brush)Application.Current.Resources["SkyBrush"];
        private static readonly System.Windows.Media.Brush Mint = (System.Windows.Media.Brush)Application.Current.Resources["MintBrush"];
        private static readonly System.Windows.Media.Brush Sakura = (System.Windows.Media.Brush)Application.Current.Resources["SakuraBrush"];
        private static readonly System.Windows.Media.Brush Rose = (System.Windows.Media.Brush)Application.Current.Resources["RoseBrush"];
        private static readonly System.Windows.Media.Brush Lavender = (System.Windows.Media.Brush)Application.Current.Resources["LavenderBrush"];
        private static readonly System.Windows.Media.Brush Muted = (System.Windows.Media.Brush)Application.Current.Resources["MutedBrush"];
        private static readonly System.Windows.Media.Brush Soft = (System.Windows.Media.Brush)Application.Current.Resources["SoftBrush"];
        private static readonly System.Windows.Media.Brush ChipBg = (System.Windows.Media.Brush)Application.Current.Resources["ChipBrush"];
        private static readonly System.Windows.Media.Brush ChipLiveBg = Frozen(System.Windows.Media.Color.FromArgb(0x1F, 0x7E, 0xE8, 0xC4));
        private static readonly System.Windows.Media.Brush ChipBadBg = Frozen(System.Windows.Media.Color.FromArgb(0x1F, 0xFF, 0x8A, 0x8A));

        private static System.Windows.Media.Brush Frozen(System.Windows.Media.Color c)
        {
            var b = new System.Windows.Media.SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private bool TvConfigured => System.Net.IPAddress.TryParse(_settings.TvIp ?? "", out _);

        private void UpdateStatusLabel()
        {
            bool running = _monitor.IsRunning;
            bool active = _monitor.IsDisplayActive;
            bool setup = !TvConfigured;
            var trigger = _settings.WatchedDevices.FirstOrDefault(d => d.Enabled);
            int moreTriggers = Math.Max(0, _settings.WatchedDevices.Count(d => d.Enabled) - 1);

            int hour = DateTime.Now.Hour;
            LblKana.Text = Tracked(Loc.T(hour >= 5 && hour < 11 ? "Home.Kana.Morning" : hour >= 11 && hour < 18 ? "Home.Kana.Day" : "Home.Kana.Evening"));

            // What the banner says, most specific first: an action in progress, not set up yet, TV
            // on, TV not answering, waiting for a controller, monitoring off.
            string phase = _monitor.PhaseKey;
            bool unreachable = !setup && _lastAdbReachable == false && !active;
            string state = phase != null ? (phase == "Phase.Back" ? "Returning" : "Busy")
                : setup ? "Setup" : active ? "Active" : unreachable ? "Unreachable" : running ? "Ready" : "Paused";
            string title = Loc.T("Home.Title." + state);
            string text = state == "Busy" || state == "Returning" ? Loc.T(phase, _monitor.PhaseArg)
                : state == "Ready" ? (trigger != null ? Loc.T("Home.Text.Ready", trigger.DisplayName) : Loc.T("Home.Text.ReadyNoDevice"))
                : state == "Unreachable" ? Loc.T("Home.Text.Unreachable", _settings.TvIp)
                : Loc.T("Home.Text." + state);
            SetHeroText(title, text);

            string pill = Loc.T(state == "Busy" || state == "Returning" ? "Home.Status.Busy" : setup ? "Home.Status.Setup" : active ? "Home.Status.Active"
                : unreachable ? "Home.Status.Unreachable" : running ? "Home.Status.Waiting" : "Home.Status.Paused");
            if (!setup) pill += "  ·  " + TvDisplayName() + "  ·  HDMI " + _settings.HdmiPort;
            LblStatus.Text = pill;
            var pillColor = ((System.Windows.Media.SolidColorBrush)(state == "Busy" || state == "Returning" ? Lavender
                : active ? Sakura : unreachable ? Rose : running && !setup ? Sky : Muted)).Color;
            SetPillColor(pillColor, glow: pillColor != ((System.Windows.Media.SolidColorBrush)Muted).Color);

            BtnActivateNow.Visibility = BtnDeactivateNow.Visibility = setup ? Visibility.Collapsed : Visibility.Visible;
            BtnHomeSetup.Visibility = setup ? Visibility.Visible : Visibility.Collapsed;
            BtnActivateNow.IsEnabled = !active;

            // Cards
            LblTriggerName.Text = trigger?.DisplayName ?? Loc.T("Home.Triggers.None");
            LblTriggerSub.Text = trigger == null ? Loc.T("Home.Triggers.NoneSub")
                : moreTriggers > 0 ? Loc.T("Home.Triggers.More", moreTriggers) : Loc.T("Home.Triggers.One");
            bool triggerOn = trigger != null && _lastScan.Any(d => d.IsConnected && _settings.WatchedDevices.Any(w => w.Enabled && w.Key == d.Key));
            SetChip(ChipTrigger, LblTriggerChip, trigger == null ? Loc.T("Chip.Manage") : triggerOn ? Loc.T("Chip.Connected") : Loc.T("Chip.Offline"),
                    triggerOn ? (bool?)true : null);

            LblTvName.Text = setup ? Loc.T("Home.Tv.None") : TvDisplayName();
            LblTvSub.Text = setup ? Loc.T("Home.Tv.NoneSub") : Loc.T("Home.Tv.Sub", _settings.TvIp, _settings.HdmiPort);
            ChipTv.Visibility = setup ? Visibility.Collapsed : Visibility.Visible;
            SetChip(ChipTv, LblTvChip, _lastAdbReachable == true ? Loc.T("Chip.Connected") : _lastAdbReachable == false ? Loc.T("Chip.Unreachable") : "…",
                    _lastAdbReachable);

            // Sidebar footer
            LblNavStatus.Text = Loc.T(setup ? "Nav.TvNone" : _lastAdbReachable == true ? "Nav.TvOk" : _lastAdbReachable == false ? "Nav.TvBad" : "Nav.TvUnknown");
            var led = setup || _lastAdbReachable == null ? Muted : _lastAdbReachable.Value ? Mint : Rose;
            NavLed.Fill = led;
            NavLedGlow.Color = ((System.Windows.Media.SolidColorBrush)led).Color;
            NavLedGlow.Opacity = led == Muted ? 0 : 0.9;

            // Settings → TV address
            LblAdb.Text = setup ? Loc.T(string.IsNullOrWhiteSpace(_settings.TvIp) ? "Set.Adb.NotSet" : "Set.Adb.Invalid")
                : Loc.T(_lastAdbReachable == true ? "Set.Adb.Ok" : _lastAdbReachable == false ? "Set.Adb.Bad" : "Set.Adb.Checking");
            LblAdb.Foreground = setup || _lastAdbReachable == null ? Muted : _lastAdbReachable.Value ? Mint : Rose;

            if (_tray != null) _tray.Text = BuildTrayTooltip();
        }

        // WPF has no letter-spacing; thin spaces between the characters give the small kana label
        // the airy tracking it has in the design.
        private static string Tracked(string s) => string.Join(" ", s.Select(c => c.ToString()));

        // Banner text changes cross-fade instead of snapping: out, swap, then in from a few px below.
        private string _heroTitle, _heroText;

        private void SetHeroText(string title, string text)
        {
            if (title == _heroTitle && text == _heroText) return;
            bool first = _heroTitle == null;
            _heroTitle = title;
            _heroText = text;
            if (first || !IsVisible) { LblHeroTitle.Text = title; LblHeroText.Text = text; return; }

            var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
            var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(130)));
            fadeOut.Completed += (s, e) =>
            {
                LblHeroTitle.Text = _heroTitle;
                LblHeroText.Text = _heroText;
                var shift = HeroCopy.RenderTransform as System.Windows.Media.TranslateTransform;
                if (shift == null) HeroCopy.RenderTransform = shift = new System.Windows.Media.TranslateTransform();
                HeroCopy.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(260))) { EasingFunction = ease });
                shift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(8, 0, new Duration(TimeSpan.FromMilliseconds(320))) { EasingFunction = ease });
            };
            HeroCopy.BeginAnimation(OpacityProperty, fadeOut);
        }

        // The status pill glides to its new colour (fill, border, LED, text) rather than jumping.
        private System.Windows.Media.SolidColorBrush _pillText, _pillBg, _pillBorder;
        private System.Windows.Media.Color? _pillColor;

        private void SetPillColor(System.Windows.Media.Color c, bool glow)
        {
            if (_pillText == null)
            {
                _pillText = new System.Windows.Media.SolidColorBrush(c);
                _pillBg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x1A, c.R, c.G, c.B));
                _pillBorder = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x47, c.R, c.G, c.B));
                LblStatus.Foreground = StatusLed.Fill = _pillText;
                StatusPill.Background = _pillBg;
                StatusPill.BorderBrush = _pillBorder;
            }
            StatusGlow.Opacity = glow ? 0.9 : 0;
            if (_pillColor == c) return;
            _pillColor = c;
            StatusGlow.Color = c;
            var time = new Duration(TimeSpan.FromMilliseconds(450));
            _pillText.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty, new System.Windows.Media.Animation.ColorAnimation(c, time));
            _pillBg.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty,
                new System.Windows.Media.Animation.ColorAnimation(System.Windows.Media.Color.FromArgb(0x1A, c.R, c.G, c.B), time));
            _pillBorder.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty,
                new System.Windows.Media.Animation.ColorAnimation(System.Windows.Media.Color.FromArgb(0x47, c.R, c.G, c.B), time));
        }

        private static void SetChip(Border chip, TextBlock text, string label, bool? good)
        {
            text.Text = label;
            chip.Background = good == true ? ChipLiveBg : good == false ? ChipBadBg : ChipBg;
            text.Foreground = good == true ? Mint : good == false ? Rose : Soft;
        }

        // "Mi TV" from the display's EDID; the model from ADB isn't known without asking the TV.
        private string _tvNameCache;
        private int _tvNameFor = -1;
        private string TvDisplayName()
        {
            if (_tvNameFor != _settings.TargetDisplay)
            {
                _tvNameFor = _settings.TargetDisplay;
                _tvNameCache = DisplayManager.GetFriendlyName(_settings.TargetDisplay);
            }
            return string.IsNullOrEmpty(_tvNameCache) ? "TV" : _tvNameCache;
        }

        private string TrayStatusText() => Loc.T(_monitor.IsDisplayActive ? "Tray.Status.Active" : _monitor.IsRunning ? "Tray.Status.Waiting" : "Tray.Status.Paused");

        private string BuildTrayTooltip()
        {
            string tv = Loc.T(_lastAdbReachable == null ? "Tray.Tip.Checking" : _lastAdbReachable.Value ? "Tray.Tip.Ok" : "Tray.Tip.Bad");
            string tip = Loc.T("Tray.Tip", TrayStatusText(), tv);
            return tip.Length > 127 ? tip.Substring(0, 127) : tip;   // NotifyIcon.Text is capped at 127 chars
        }

        private void SetAdbReachable(bool? reachable)
        {
            _lastAdbReachable = reachable;
            if (_tray != null)
                _tray.Icon = reachable == true ? _trayIconOk : reachable == false ? _trayIconBad : _trayIconUnknown;
            UpdateStatusLabel();
        }

        private void OnLanguageChanged()
        {
            LblVersion.Text = Loc.T("Set.Version", GetVersionString());
            UpdateMqttPowerInfo();
            OnMqttStatus(_ha.LastStatus ?? Loc.T("Ha.Off"));
            RefreshTrayTexts();
            RefreshDeviceTexts();
            UpdateHotkeyFields();
            UpdateHotkeyHints();
            FillGrace();
            RenderRecent();
            UpdateStatusLabel();
        }

        // -------------------------------------------------------------------------
        // Log — the full log on the Diagnostics page, a short "Recent" list on Home
        // -------------------------------------------------------------------------

        private readonly List<Tuple<DateTime, string>> _recent = new List<Tuple<DateTime, string>>();

        private void AppendLog(string msg)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => AppendLog(msg))); return; }
            AppLog.Write(msg);
            TxtLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + Environment.NewLine);
            if (TxtLog.Text.Length > 20000)
                TxtLog.Text = TxtLog.Text.Substring(TxtLog.Text.Length - 12000);
            TxtLog.ScrollToEnd();

            // Home shows what happened, not every step of it.
            if (msg.StartsWith("[Test]") || msg.StartsWith("SetHighestRefreshRate") || msg.StartsWith("DisableAllExcept") ||
                msg.StartsWith("Waiting ") || msg.StartsWith("Setup:")) return;
            _recent.Insert(0, Tuple.Create(DateTime.Now, msg));
            if (_recent.Count > 6) _recent.RemoveAt(_recent.Count - 1);
            RenderRecent();
        }

        private void RenderRecent()
        {
            LblRecent.Inlines.Clear();
            if (_recent.Count == 0) { LblRecent.Inlines.Add(new System.Windows.Documents.Run(Loc.T("Home.Recent.Empty"))); return; }
            for (int i = 0; i < _recent.Count; i++)
            {
                if (i > 0) LblRecent.Inlines.Add(new System.Windows.Documents.LineBreak());
                LblRecent.Inlines.Add(new System.Windows.Documents.Run(_recent[i].Item1.ToString("HH:mm") + "  ") { Foreground = Muted, FontSize = 11 });
                LblRecent.Inlines.Add(new System.Windows.Documents.Run(_recent[i].Item2) { Foreground = i == 0 ? Soft : Muted });
            }
        }

        // Surfaces failures worth interrupting the user for, even when the window is hidden in
        // the tray and nobody is looking at the log.
        private void ShowTrayBalloon(string title, string message)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => ShowTrayBalloon(title, message))); return; }
            _tray?.ShowBalloonTip(6000, title, message, Forms.ToolTipIcon.Warning);
        }

        // -------------------------------------------------------------------------
        // Window event handlers (wired from MainWindow.xaml)
        // -------------------------------------------------------------------------

        private void OnStateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized) HideToTray();
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (!_reallyExit)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            _statusTimer?.Stop();
            _hotkeys?.Dispose();
            Loc.Changed -= OnLanguageChanged;
            _monitor.Log -= AppendLog;
            _monitor.RunningChanged -= OnRunningChanged;
            _monitor.Notify -= ShowTrayBalloon;
            _ha.Stop();
            _monitor.Stop();
            if (_tray != null) _tray.Visible = false;
            _trayIconUnknown?.Dispose();
            _trayIconOk?.Dispose();
            _trayIconBad?.Dispose();
            if (_devNotifyHandle != IntPtr.Zero) Native.UnregisterDeviceNotification(_devNotifyHandle);
        }

        // 0 Home · 1 Devices · 2 Settings · 3 Home Assistant · 4 Diagnostics
        private int CurrentPage { get; set; }

        private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (NavList.SelectedIndex < 0) return;
            CurrentPage = NavList.SelectedIndex;
            for (int i = 0; i < Pages.Children.Count; i++)
                Pages.Children[i].Visibility = i == CurrentPage ? Visibility.Visible : Visibility.Collapsed;
            AnimatePageIn(Pages.Children[CurrentPage]);
            if (CurrentPage == 1) ScanDevices();
            if (CurrentPage == 2) FillDisplays(TargetDisplayUi ?? _settings.TargetDisplay);
        }

        // The new page fades in and settles up from a few pixels below.
        private static void AnimatePageIn(UIElement page)
        {
            var shift = page.RenderTransform as System.Windows.Media.TranslateTransform;
            if (shift == null) page.RenderTransform = shift = new System.Windows.Media.TranslateTransform();
            var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
            var time = new Duration(TimeSpan.FromMilliseconds(220));
            page.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, time) { EasingFunction = ease });
            shift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(10, 0, time) { EasingFunction = ease });
        }

        private void GoTo(int page) => NavList.SelectedIndex = page;
        private void GoDevices_Click(object sender, RoutedEventArgs e) => GoTo(1);

        private void CmbLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingUi || !(CmbLanguage.SelectedItem is ComboBoxItem item)) return;
            _settings.Language = (string)item.Tag;
            _settings.Save();
            Loc.Set(_settings.Language);
        }

        private void ChkAutostart_CheckedChanged(object sender, RoutedEventArgs e) => SetAutostart(ChkAutostart.IsChecked == true);

        private void BtnActivateNow_Click(object sender, RoutedEventArgs e) => ActivateNow();
        private void BtnDeactivateNow_Click(object sender, RoutedEventArgs e) => DeactivateNow();
        private void BtnRemove_Click(object sender, RoutedEventArgs e) => RemoveSelectedDevice();
        private void BtnAdd_Click(object sender, RoutedEventArgs e) => AddSelectedDevice();

        private void OpenLogFile_Click(object sender, RoutedEventArgs e) => OpenUrl(AppLog.FilePath);
        private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            BtnCheckUpdates.IsEnabled = false;
            try { await CheckForUpdatesAsync(manual: true); }
            finally { BtnCheckUpdates.IsEnabled = true; }
        }
        private void GitHub_Click(object sender, RoutedEventArgs e) => OpenUrl(Links.Repository);
        private void Support_Click(object sender, RoutedEventArgs e) => OpenUrl(Links.Support);

        private static void OpenUrl(string target)
        {
            if (string.IsNullOrEmpty(target)) return;
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
        }
    }

}
