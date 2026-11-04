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

            var icon = LoadAppIcon();
            Icon = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            Title = "SteamTV Monitor " + GetVersionString();

            _settings = AppSettings.Load();
            _monitor = new MonitorService(_settings);
            _monitor.Log += AppendLog;
            _monitor.RunningChanged += OnRunningChanged;
            _monitor.Notify += ShowTrayBalloon;
            _monitor.DisplayActiveChanged += _ => Dispatcher.BeginInvoke(new Action(UpdateStatusLabel));
            _monitor.TvAddressChanged += ip => Dispatcher.BeginInvoke(new Action(() =>
            {
                TxtIp.Text = ip;
                ShowTrayBalloon("SteamTV", "The TV got a new IP address (" + ip + ") — settings updated.");
            }));
            _ha = new HomeAssistantMqtt(_monitor, GetVersionString());
            _ha.Log += AppendLog;
            _ha.StatusChanged += OnMqttStatus;
            _ha.Command += OnHaCommand;
            _ha.PowerCommand += OnHaPowerCommand;

            LoadSettingsToUi();
            InitDevicesTab();
            BuildTestButtons();
            BuildTray();
            UpdateStatusLabel();
            SetThemeButtons(system: true);
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
                Loaded += (s, e) => { HideToTray(); if (_settings.AutoMonitorOnStart) StartMonitor(); };
            }
            else if (_settings.IsFirstRun)
                Loaded += async (s, e) => await RunAutoSetupAsync(firstRun: true);
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

        private async Task CheckForUpdatesAsync()
        {
            string tag = await UpdateChecker.GetLatestTagAsync();
            var current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (!UpdateChecker.IsNewer(tag, current)) return;

            Dispatcher.Invoke(() =>
            {
                _updateAvailable = true;
                _updateTag = tag;
                if (_miTrayUpdate != null) { _miTrayUpdate.Text = "Install update " + tag; _miTrayUpdate.Visible = true; }
                AppendLog("Update available: " + tag + " (running v" + current.Major + "." + current.Minor + "." + current.Build + ") — " + UpdateChecker.ReleasesPageUrl);
                _tray?.ShowBalloonTip(8000, "SteamTV update available",
                    "Version " + tag + " is out — click to install it.", Forms.ToolTipIcon.Info);
            });
        }

        private bool _updating;

        private async void InstallUpdate()
        {
            if (_updating || _updateTag == null) return;
            if (System.Windows.MessageBox.Show("Install SteamTV " + _updateTag + " now? SteamTV restarts; an active TV session is restored first.",
                    "SteamTV update", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            _updating = true;
            AppendLog("Downloading " + _updateTag + "…");
            var restoring = _monitor.IsDisplayActive ? _monitor.DeactivateNow() : Task.CompletedTask;
            string err = await Updater.InstallAsync(_updateTag, startHidden: !IsVisible);
            await restoring;   // never exit halfway through putting the monitors back
            _updating = false;
            if (err == null) { AppendLog("Update installed — restarting."); _reallyExit = true; Close(); return; }
            AppendLog("Update failed: " + err);
            if (System.Windows.MessageBox.Show("Update failed: " + err + "\n\nOpen the releases page?", "SteamTV update",
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
            TxtIp.Text = _settings.TvIp;
            NumHdmiPort.Value = Math.Max(1, Math.Min(4, _settings.HdmiPort));
            NumTarget.Value = Math.Max(1, Math.Min(64, _settings.TargetDisplay));
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
            _loadingUi = true;
            ChkMqttEnabled.IsChecked = _settings.MqttEnabled;
            ChkMqttPower.IsChecked = _settings.MqttPowerButtons;
            _loadingUi = false;
            UpdateMqttPowerInfo();
            TxtPcMac.Text = PowerControl.PrimaryMac() ?? "not found — see the adapter in ipconfig /all";
            TxtMqttHost.Text = _settings.MqttHost;
            NumMqttPort.Value = _settings.MqttPort;
            TxtMqttUser.Text = _settings.MqttUser;
            PwdMqtt.Password = _settings.MqttPassword;
        }

        private void SaveUiToSettings()
        {
            _settings.TvIp = TxtIp.Text.Trim();
            _settings.HdmiPort = (int)(NumHdmiPort.Value ?? _settings.HdmiPort);
            _settings.TargetDisplay = (int)(NumTarget.Value ?? _settings.TargetDisplay);
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
        }

        // Shared by Start and the manual Activate/Deactivate buttons — all of them need a valid IP.
        // Remote (Home Assistant) commands pass interactive: false — nobody may be at the PC to
        // dismiss a message box, so the problem goes to the log and a tray balloon instead.
        private bool ValidateIpOrWarn(bool interactive = true)
        {
            string problem = null;
            System.Net.IPAddress addr;
            if (string.IsNullOrWhiteSpace(TxtIp.Text))
                problem = "No TV set yet — click \"Set up automatically\" on the Monitor tab.";
            else if (!System.Net.IPAddress.TryParse(TxtIp.Text.Trim(), out addr))
                problem = "Invalid IP address format.";
            if (problem == null) return true;

            if (interactive)
                System.Windows.MessageBox.Show(problem, "SteamTV", MessageBoxButton.OK, MessageBoxImage.Warning);
            else
            {
                AppendLog("Command ignored: " + problem);
                ShowTrayBalloon("SteamTV", "Command ignored: " + problem);
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
            UpdateStatusLabel();
            bool canEdit = !running;
            TxtIp.IsEnabled = NumHdmiPort.IsEnabled = NumTarget.IsEnabled = canEdit;
            ChkDisableOthers.IsEnabled = ChkWakeTV.IsEnabled = ChkSourceSwitch.IsEnabled = canEdit;
            ChkBigPicture.IsEnabled = ChkSwitchAudio.IsEnabled = ChkHdr.IsEnabled = canEdit;
            ChkRestoreTvApp.IsEnabled = ChkSleepTv.IsEnabled = canEdit;
            BtnAutoSetup.IsEnabled = BtnFindTv.IsEnabled = BtnDetectDisplay.IsEnabled = canEdit;
            BtnStart.IsEnabled = canEdit;
            BtnStop.IsEnabled = running;
        }

        private void OnStatusTick()
        {
            UpdateStatusLabel();

            // Live device status — only while someone can actually see the Devices tab.
            if (IsVisible && Tabs.SelectedIndex == 1 && ++_scanTick % 2 == 0)
                ScanDevices();

            // ADB check: run every 15 s in a background task
            if (!_adbCheckPending && (DateTime.Now - _lastAdbCheck).TotalSeconds >= 15)
            {
                System.Net.IPAddress addr;
                if (!System.Net.IPAddress.TryParse(_settings.TvIp ?? "", out addr))
                {
                    _lastAdbCheck = DateTime.Now;
                    LblAdb.Text = "● ADB  invalid IP";
                    LblAdb.Foreground = System.Windows.Media.Brushes.OrangeRed;
                    SetAdbReachable(false);
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
                            LblAdb.Text = "● ADB  ↺15s";
                            LblAdb.Foreground = ok ? System.Windows.Media.Brushes.Green : System.Windows.Media.Brushes.OrangeRed;
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
                                ShowTrayBalloon("SteamTV", "TV unreachable via ADB for a while — check that Wireless debugging is still on in the TV's Developer Options (some TVs disable it again after a reboot).");
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
            ShowTrayBalloon("SteamTV", "The TV got a new IP address (" + ip + ") — settings updated.");
        }

        private void UpdateStatusLabel()
        {
            bool running = _monitor.IsRunning;
            bool active = _monitor.IsDisplayActive;
            LblStatus.Text = (running ? "● Running" : "● Stopped") + (active ? " — TV active" : "");
            LblStatus.Foreground = running ? System.Windows.Media.Brushes.Green : System.Windows.Media.Brushes.OrangeRed;
            if (_tray != null)
                _tray.Text = BuildTrayTooltip(running);
            BtnActivateNow.IsEnabled = !active;
        }

        private string BuildTrayTooltip(bool running)
        {
            string adb = _lastAdbReachable == null ? "checking…" : (_lastAdbReachable.Value ? "reachable" : "not reachable");
            // NotifyIcon.Text is capped at 127 chars by Windows.
            return "SteamTV — " + (running ? "running" : "stopped") + " | ADB: " + adb;
        }

        private void SetAdbReachable(bool reachable)
        {
            _lastAdbReachable = reachable;
            if (_tray != null)
            {
                _tray.Icon = reachable ? _trayIconOk : _trayIconBad;
                _tray.Text = BuildTrayTooltip(_monitor.IsRunning);
            }
        }

        // -------------------------------------------------------------------------
        // Log
        // -------------------------------------------------------------------------

        private void AppendLog(string msg)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => AppendLog(msg))); return; }
            AppLog.Write(msg);
            TxtLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + Environment.NewLine);
            if (TxtLog.Text.Length > 20000)
                TxtLog.Text = TxtLog.Text.Substring(TxtLog.Text.Length - 12000);
            TxtLog.ScrollToEnd();
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

        private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.OriginalSource == Tabs && Tabs.SelectedIndex == 1) ScanDevices();
        }

        private void ChkAutostart_CheckedChanged(object sender, RoutedEventArgs e) => SetAutostart(ChkAutostart.IsChecked == true);

        private void BtnStart_Click(object sender, RoutedEventArgs e) => StartMonitor();
        private void BtnStop_Click(object sender, RoutedEventArgs e) => _monitor.Stop();
        private void BtnExit_Click(object sender, RoutedEventArgs e) { _reallyExit = true; Close(); }

        private void BtnActivateNow_Click(object sender, RoutedEventArgs e) => ActivateNow();
        private void BtnDeactivateNow_Click(object sender, RoutedEventArgs e) => DeactivateNow();
        private void BtnDisplays_Click(object sender, RoutedEventArgs e) => ShowDisplaysList();
        private void BtnRemove_Click(object sender, RoutedEventArgs e) => RemoveSelectedDevice();
        private void BtnAdd_Click(object sender, RoutedEventArgs e) => AddSelectedDevice();

        // -------------------------------------------------------------------------
        // Theme switcher
        // -------------------------------------------------------------------------

        private void SetThemeButtons(bool light = false, bool dark = false, bool system = false)
        {
            BtnThemeLight.Appearance = light ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
            BtnThemeDark.Appearance = dark ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
            BtnThemeSystem.Appearance = system ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
        }

        private void BtnThemeLight_Click(object sender, RoutedEventArgs e)
        {
            SystemThemeWatcher.UnWatch(this);
            // Backdrop defaults to Mica, which needs ExtendsContentIntoTitleBar — without it the
            // window just renders black. Must stay None here, same as the system-theme path below.
            ApplicationThemeManager.Apply(ApplicationTheme.Light, Wpf.Ui.Controls.WindowBackdropType.None);
            SetThemeButtons(light: true);
        }

        private void BtnThemeDark_Click(object sender, RoutedEventArgs e)
        {
            SystemThemeWatcher.UnWatch(this);
            ApplicationThemeManager.Apply(ApplicationTheme.Dark, Wpf.Ui.Controls.WindowBackdropType.None);
            SetThemeButtons(dark: true);
        }

        private void BtnThemeSystem_Click(object sender, RoutedEventArgs e)
        {
            // ApplySystemTheme() always calls Apply(...) with its default (Mica) backdrop internally,
            // which it pushes straight to Application.Current.MainWindow — breaking this chrome-less
            // window the same way the Light/Dark buttons did, and setting WindowBackdropType back
            // afterward doesn't undo it (UpdateBackground already made its own DWM call by then).
            // So: replicate ApplySystemTheme()'s own Dark/Light mapping and call Apply(..., None) directly.
            var systemTheme = ApplicationThemeManager.GetSystemTheme();
            var mappedTheme = systemTheme is SystemTheme.Dark or SystemTheme.CapturedMotion or SystemTheme.Glow
                ? ApplicationTheme.Dark
                : ApplicationTheme.Light;
            ApplicationThemeManager.Apply(mappedTheme, Wpf.Ui.Controls.WindowBackdropType.None);
            SystemThemeWatcher.Watch(this, Wpf.Ui.Controls.WindowBackdropType.None);
            SetThemeButtons(system: true);
        }
    }

}
