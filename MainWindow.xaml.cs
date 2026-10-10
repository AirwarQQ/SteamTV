// Main window: tab-based UI — Monitor settings, Gamepad management, Test actions.
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

            LoadSettingsToUi();
            InitDevicesTab();
            BuildTestButtons();
            BuildTray();
            UpdateStatusLabel();
            SetThemeButtons(system: true);

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

        private async Task CheckForUpdatesAsync()
        {
            string tag = await UpdateChecker.GetLatestTagAsync();
            var current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (!UpdateChecker.IsNewer(tag, current)) return;

            Dispatcher.Invoke(() =>
            {
                _updateAvailable = true;
                AppendLog("Update available: " + tag + " (running v" + current.Major + "." + current.Minor + "." + current.Build + ") — " + UpdateChecker.ReleasesPageUrl);
                _tray?.ShowBalloonTip(8000, "SteamTV update available",
                    "Version " + tag + " is out — click to open the releases page.", Forms.ToolTipIcon.Info);
            });
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
                adb.SwitchSourceToPc(_settings.HdmiSourcePackage);
                return "done (port " + _settings.HdmiPort + ")";
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

        private void LoadSettingsToUi()
        {
            TxtIp.Text = _settings.TvIp;
            NumHdmiPort.Value = Math.Max(1, Math.Min(4, _settings.HdmiPort));
            NumTarget.Value = Math.Max(1, Math.Min(64, _settings.TargetDisplay));
            ChkDisableOthers.IsChecked = _settings.DisableOthers;
            ChkWakeTV.IsChecked = _settings.EnableWakeTV;
            ChkSourceSwitch.IsChecked = _settings.EnableSourceSwitch;
            ChkBigPicture.IsChecked = _settings.EnableBigPicture;
            ChkAutostart.IsChecked = IsAutostartEnabled();
            TxtAdbPath.Text = _settings.AdbPath;
            TxtSteamPath.Text = _settings.SteamPath;
            TxtHdmiActivityPattern.Text = _settings.HdmiActivityPattern;
            TxtTvHomeComponent.Text = _settings.TvHomeComponent;
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
            _settings.AdbPath = string.IsNullOrWhiteSpace(TxtAdbPath.Text) ? "adb.exe" : TxtAdbPath.Text.Trim();
            _settings.SteamPath = TxtSteamPath.Text.Trim();
            _settings.HdmiActivityPattern = TxtHdmiActivityPattern.Text.Trim();
            _settings.TvHomeComponent = TxtTvHomeComponent.Text.Trim();
            _settings.Save();
        }

        // Shared by Start and the manual Activate/Deactivate buttons — all of them need a valid IP.
        private bool ValidateIpOrWarn()
        {
            if (string.IsNullOrWhiteSpace(TxtIp.Text))
            {
                System.Windows.MessageBox.Show("Enter the TV IP address.", "SteamTV",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            System.Net.IPAddress addr;
            if (!System.Net.IPAddress.TryParse(TxtIp.Text.Trim(), out addr))
            {
                System.Windows.MessageBox.Show("Invalid IP address format.", "SteamTV",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            return true;
        }

        private void StartMonitor()
        {
            if (!ValidateIpOrWarn()) return;
            SaveUiToSettings();
            _monitor.Start();
        }

        // -------------------------------------------------------------------------
        // Devices tab
        // -------------------------------------------------------------------------

        public sealed class DeviceRow : INotifyPropertyChanged
        {
            private static readonly System.Windows.Media.Brush ConnectedBrush = System.Windows.Media.Brushes.LimeGreen;
            private static readonly System.Windows.Media.Brush DisconnectedBrush = System.Windows.Media.Brushes.Gray;

            private string _name, _category, _bus;
            private bool _enabled, _connected, _watched;

            public string Key { get; set; }
            internal WatchedDevice Watched { get; set; }   // set only on rows of the trigger list

            private string _detail;
            public string Detail { get => _detail; set => Set(ref _detail, value, nameof(Detail)); }   // row tooltip

            public string Name { get => _name; set => Set(ref _name, value, nameof(Name)); }
            public string Bus { get => _bus; set => Set(ref _bus, value, nameof(Bus)); }
            public string Category
            {
                get => _category;
                set { if (Set(ref _category, value, nameof(Category))) Raise(nameof(CategoryRank)); }
            }
            public int CategoryRank
            {
                get { int i = Array.IndexOf(DeviceScanner.CategoryOrder, _category); return i < 0 ? 99 : i; }
            }
            public bool Enabled { get => _enabled; set => Set(ref _enabled, value, nameof(Enabled)); }
            public bool IsWatched { get => _watched; set => Set(ref _watched, value, nameof(IsWatched)); }
            public bool IsConnected
            {
                get => _connected;
                set { if (Set(ref _connected, value, nameof(IsConnected))) { Raise(nameof(Status)); Raise(nameof(StatusBrush)); } }
            }
            public string Status => _connected ? "● Connected" : "○ Offline";
            public System.Windows.Media.Brush StatusBrush => _connected ? ConnectedBrush : DisconnectedBrush;

            public event PropertyChangedEventHandler PropertyChanged;
            private void Raise(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
            private bool Set<T>(ref T field, T value, string p)
            {
                if (Equals(field, value)) return false;
                field = value;
                Raise(p);
                return true;
            }
        }

        private const string AllCategories = "All categories";
        private readonly System.Collections.ObjectModel.ObservableCollection<DeviceRow> _watchedRows =
            new System.Collections.ObjectModel.ObservableCollection<DeviceRow>();
        private readonly System.Collections.ObjectModel.ObservableCollection<DeviceRow> _deviceRows =
            new System.Collections.ObjectModel.ObservableCollection<DeviceRow>();
        private List<ExternalDevice> _lastScan = new List<ExternalDevice>();
        private bool _scanPending;
        private int _scanTick;

        // -------------------------------------------------------------------------
        // Device-change notifications — instant reaction instead of waiting for the next poll
        // -------------------------------------------------------------------------

        private IntPtr _devNotifyHandle;
        private DispatcherTimer _devChangeDebounce;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
            _devNotifyHandle = Native.RegisterForAllDeviceInterfaces(hwnd);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_DEVICECHANGE)
            {
                long ev = wParam.ToInt64();
                if (ev == Native.DBT_DEVNODES_CHANGED || ev == Native.DBT_DEVICEARRIVAL || ev == Native.DBT_DEVICEREMOVECOMPLETE)
                {
                    _monitor.NotifyDevicesChanged();
                    // One plug-in fires a burst of these; refresh the grids once it settles.
                    _devChangeDebounce.Stop();
                    _devChangeDebounce.Start();
                }
            }
            return IntPtr.Zero;
        }

        private void InitDevicesTab()
        {
            _devChangeDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _devChangeDebounce.Tick += (s, e) =>
            {
                _devChangeDebounce.Stop();
                if (IsVisible && Tabs.SelectedIndex == 1) ScanDevices();
            };

            GridWatched.ItemsSource = _watchedRows;
            GridConnected.ItemsSource = _deviceRows;

            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_deviceRows);
            view.SortDescriptions.Add(new SortDescription(nameof(DeviceRow.CategoryRank), ListSortDirection.Ascending));
            view.SortDescriptions.Add(new SortDescription(nameof(DeviceRow.Name), ListSortDirection.Ascending));
            view.Filter = o =>
            {
                string cat = CmbCategory.SelectedItem as string;
                return cat == null || cat == AllCategories || ((DeviceRow)o).Category == cat;
            };

            CmbCategory.Items.Add(AllCategories);
            foreach (var c in DeviceScanner.CategoryOrder) CmbCategory.Items.Add(c);
            CmbCategory.SelectedIndex = 0;

            RebuildWatchedRows();
        }

        private void RebuildWatchedRows()
        {
            _watchedRows.Clear();
            foreach (var w in _settings.WatchedDevices)
            {
                var row = new DeviceRow
                {
                    Key = w.Key, Watched = w, Name = w.DisplayName, Category = w.Category, Enabled = w.Enabled,
                    Detail = WatchedDetail(w),
                };
                // Hooked after the initial values so building the list doesn't trigger saves.
                row.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(DeviceRow.Enabled)) OnWatchedEnabledChanged((DeviceRow)s); };
                _watchedRows.Add(row);
            }
            ApplyScanToRows();
        }

        private static string WatchedDetail(WatchedDevice w)
        {
            string what = w.Action == TriggerAction.TvOnly ? "On connect: TV only" : "On connect: TV + Big Picture";
            if (w.RestoreOnDisconnect) what += " · restores on disconnect";
            return (string.IsNullOrWhiteSpace(w.Alias) ? "" : w.Name + " · ") + w.Key + "\n" + what;
        }

        // Rescans in the background; skips if a scan is already running.
        private void ScanDevices()
        {
            if (_scanPending) return;
            _scanPending = true;
            Task.Run(() =>
            {
                List<ExternalDevice> devices = null;
                string error = null;
                try { devices = DeviceScanner.Scan(); }
                catch (Exception ex) { error = ex.Message; }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _scanPending = false;
                    if (error != null) { AppendLog("Device scan failed: " + error); return; }
                    _lastScan = devices;
                    ApplyScanToRows();
                }));
            });
        }

        // Merges the latest scan into both grids in place, so selection and sort order survive live updates.
        private void ApplyScanToRows()
        {
            var byKey = _lastScan.ToDictionary(d => d.Key, StringComparer.OrdinalIgnoreCase);
            var watchedKeys = new HashSet<string>(_settings.WatchedDevices.Select(w => w.Key), StringComparer.OrdinalIgnoreCase);

            bool dirty = false;
            foreach (var row in _watchedRows)
            {
                ExternalDevice d;
                bool found = byKey.TryGetValue(row.Key, out d);
                row.IsConnected = found && d.IsConnected;
                // Entries saved by older versions carry WMI's generic names ("HID-compliant game
                // controller") — pick up the real product name the first time the device is seen.
                // The user's own alias is never touched — only the product name underneath it.
                if (found && row.Watched != null && (row.Watched.Name != d.Name || row.Watched.Category != d.Category))
                {
                    row.Watched.Name = d.Name;
                    row.Watched.Category = row.Category = d.Category;
                    row.Name = row.Watched.DisplayName;
                    row.Detail = WatchedDetail(row.Watched);
                    if (row == _panelRow) TxtAlias.PlaceholderText = d.Name;
                    dirty = true;
                }
            }
            if (dirty) _settings.Save();

            var existing = _deviceRows.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var row in existing.Values.Where(r => !byKey.ContainsKey(r.Key)).ToList())
                _deviceRows.Remove(row);
            foreach (var d in _lastScan)
            {
                DeviceRow row;
                if (!existing.TryGetValue(d.Key, out row))
                    _deviceRows.Add(row = new DeviceRow { Key = d.Key });
                row.Name = d.Name;
                row.Detail = d.Key;
                row.Category = d.Category;
                row.Bus = d.Bus;
                row.IsConnected = d.IsConnected;
                row.IsWatched = watchedKeys.Contains(d.Key);
            }

            LblNoDevices.Visibility = _settings.WatchedDevices.Any(w => w.Enabled) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void AddSelectedDevice()
        {
            var row = GridConnected.SelectedItem as DeviceRow;
            if (row == null) return;
            if (_settings.WatchedDevices.Any(w => string.Equals(w.Key, row.Key, StringComparison.OrdinalIgnoreCase)))
            {
                GridWatched.SelectedItem = _watchedRows.FirstOrDefault(r => string.Equals(r.Key, row.Key, StringComparison.OrdinalIgnoreCase));
                return;
            }
            // Copy-on-write: the monitor thread reads this list without a lock.
            _settings.WatchedDevices = new List<WatchedDevice>(_settings.WatchedDevices)
            {
                new WatchedDevice { Key = row.Key, Name = row.Name, Category = row.Category, Enabled = true }
            };
            _settings.Save();
            RebuildWatchedRows();
        }

        private void RemoveSelectedDevice()
        {
            var row = GridWatched.SelectedItem as DeviceRow;
            if (row == null) return;
            _settings.WatchedDevices = _settings.WatchedDevices.Where(w => w != row.Watched).ToList();
            _settings.Save();
            RebuildWatchedRows();
        }

        private void OnWatchedEnabledChanged(DeviceRow row)
        {
            row.Watched.Enabled = row.Enabled;
            _settings.Save();
            LblNoDevices.Visibility = _settings.WatchedDevices.Any(w => w.Enabled) ? Visibility.Collapsed : Visibility.Visible;
        }

        // --- Selected-device settings panel -------------------------------------

        // The device the panel is currently showing. Tracked separately from GridWatched.SelectedItem
        // because clicking another row can change the selection *before* the name box loses focus —
        // committing against SelectedItem then would save the typed name onto the wrong device.
        private DeviceRow _panelRow;
        private bool _fillingPanel;

        private void GridWatched_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            CommitAlias();   // whatever was typed belongs to the previously shown device
            _panelRow = (GridWatched.SelectedItem as DeviceRow)?.Watched != null ? (DeviceRow)GridWatched.SelectedItem : null;
            PnlDevice.Visibility = _panelRow != null ? Visibility.Visible : Visibility.Collapsed;
            PnlNoSelection.Visibility = _panelRow != null ? Visibility.Collapsed : Visibility.Visible;
            if (_panelRow == null) return;

            var w = _panelRow.Watched;
            _fillingPanel = true;
            TxtAlias.Text = w.Alias ?? "";
            TxtAlias.PlaceholderText = w.Name;
            CmbAction.SelectedIndex = w.Action == TriggerAction.TvOnly ? 1 : 0;
            ChkRestoreOnDisconnect.IsChecked = w.RestoreOnDisconnect;
            _fillingPanel = false;
        }

        private void CommitAlias()
        {
            if (_panelRow == null || _fillingPanel) return;
            var w = _panelRow.Watched;
            string alias = TxtAlias.Text.Trim();
            if (alias == (w.Alias ?? "")) return;
            w.Alias = alias.Length == 0 ? null : alias;
            _settings.Save();
            _panelRow.Name = w.DisplayName;
            _panelRow.Detail = WatchedDetail(w);
        }

        private void TxtAlias_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) => CommitAlias();

        private void TxtAlias_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) CommitAlias();
            else if (e.Key == System.Windows.Input.Key.Escape && _panelRow != null) TxtAlias.Text = _panelRow.Watched.Alias ?? "";
        }

        private void CmbAction_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            e.Handled = true;
            if (_panelRow == null || _fillingPanel) return;
            _panelRow.Watched.Action = CmbAction.SelectedIndex == 1 ? TriggerAction.TvOnly : TriggerAction.TvAndBigPicture;
            _settings.Save();
            _panelRow.Detail = WatchedDetail(_panelRow.Watched);
        }

        private void ChkRestoreOnDisconnect_Changed(object sender, RoutedEventArgs e)
        {
            if (_panelRow == null || _fillingPanel) return;
            _panelRow.Watched.RestoreOnDisconnect = ChkRestoreOnDisconnect.IsChecked == true;
            _settings.Save();
            _panelRow.Detail = WatchedDetail(_panelRow.Watched);
        }

        private void CmbCategory_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
            System.Windows.Data.CollectionViewSource.GetDefaultView(_deviceRows)?.Refresh();

        private void GridConnected_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (IsOnRow(e)) AddSelectedDevice();
        }

        // Ignore double-clicks on headers / empty space under the last row.
        private static bool IsOnRow(System.Windows.Input.MouseButtonEventArgs e)
        {
            var d = e.OriginalSource as DependencyObject;
            while (d != null && !(d is DataGridRow) && !(d is System.Windows.Controls.Primitives.DataGridColumnHeader))
                d = System.Windows.Media.VisualTreeHelper.GetParent(d);
            return d is DataGridRow;
        }

        // -------------------------------------------------------------------------
        // Display list popup
        // -------------------------------------------------------------------------

        private void ShowDisplaysList()
        {
            var list = DisplayManager.ListDisplays();
            if (list.Count == 0)
            {
                System.Windows.MessageBox.Show("Failed to retrieve display list.", "Displays");
                return;
            }
            var sb = new StringBuilder();
            sb.AppendLine("Detected displays (number as in Windows Screen Settings):");
            sb.AppendLine();
            foreach (var d in list)
                sb.AppendLine("#" + d.Number + "  " +
                    (d.Active ? "[active]  " : "[off]     ") +
                    (string.IsNullOrEmpty(d.FriendlyName) ? "(name unavailable)" : d.FriendlyName));
            sb.AppendLine();
            sb.AppendLine("Enter the TV display number in \"Target display #\".");
            System.Windows.MessageBox.Show(sb.ToString(), "Displays");
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
            ChkBigPicture.IsEnabled = canEdit;
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
        // Autostart
        // -------------------------------------------------------------------------

        private static bool IsAutostartEnabled()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                    return k?.GetValue(RunValueName) != null;
            }
            catch { return false; }
        }

        private void SetAutostart(bool enable)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (k == null) return;
                    if (enable)
                        k.SetValue(RunValueName, "\"" + System.Reflection.Assembly.GetEntryAssembly().Location + "\" -autostart");
                    else
                        k.DeleteValue(RunValueName, false);
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Failed to change autostart: " + ex.Message, "SteamTV",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // -------------------------------------------------------------------------
        // Tray icon
        // -------------------------------------------------------------------------

        private static System.Drawing.Icon LoadAppIcon()
        {
            try { return System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetEntryAssembly().Location); }
            catch { return SystemIcons.Application; }
        }

        // Overlays a small colored status dot (bottom-right) on the app icon, for the tray.
        private static System.Drawing.Icon BuildStatusIcon(System.Drawing.Icon baseIcon, System.Drawing.Color? dotColor)
        {
            using (var bmp = baseIcon.ToBitmap())
            {
                if (dotColor != null)
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        int size = Math.Max(4, bmp.Width / 2);
                        var rect = new System.Drawing.Rectangle(bmp.Width - size, bmp.Height - size, size, size);
                        using (var brush = new System.Drawing.SolidBrush(dotColor.Value))
                            g.FillEllipse(brush, rect);
                        using (var pen = new System.Drawing.Pen(System.Drawing.Color.White, 1.5f))
                            g.DrawEllipse(pen, rect);
                    }
                }
                return System.Drawing.Icon.FromHandle(bmp.GetHicon());
            }
        }

        private void BuildTray()
        {
            var baseIcon = LoadAppIcon();
            _trayIconUnknown = BuildStatusIcon(baseIcon, null);
            _trayIconOk = BuildStatusIcon(baseIcon, System.Drawing.Color.LimeGreen);
            _trayIconBad = BuildStatusIcon(baseIcon, System.Drawing.Color.Red);

            _miTrayAdbStatus = new Forms.ToolStripMenuItem("ADB: —") { Enabled = false };
            _miTrayMonitorStatus = new Forms.ToolStripMenuItem("Monitoring: —") { Enabled = false };

            _miTrayAutostart = new Forms.ToolStripMenuItem("Autostart app") { CheckOnClick = true };
            _miTrayAutostart.Click += (s, e) =>
            {
                // Sync with Monitor tab checkbox — its Checked/Unchecked handler calls SetAutostart()
                ChkAutostart.IsChecked = _miTrayAutostart.Checked;
            };

            _miTrayAutoMonitor = new Forms.ToolStripMenuItem("Auto-start monitoring on launch") { CheckOnClick = true };
            _miTrayAutoMonitor.Click += (s, e) =>
            {
                _settings.AutoMonitorOnStart = _miTrayAutoMonitor.Checked;
                _settings.Save();
            };

            var menu = new Forms.ContextMenuStrip();
            menu.Opening += (s, e) =>
            {
                // Refresh checked/status state each time the menu opens
                _miTrayAutostart.Checked = IsAutostartEnabled();
                _miTrayAutoMonitor.Checked = _settings.AutoMonitorOnStart;
                _miTrayAdbStatus.Text = "ADB: " + (_lastAdbReachable == null
                    ? "checking…" : (_lastAdbReachable.Value ? "reachable ✓" : "not reachable ✗"));
                _miTrayMonitorStatus.Text = "Monitoring: " + (_monitor.IsRunning ? "running ✓" : "stopped");
            };
            menu.Items.Add(_miTrayAdbStatus);
            menu.Items.Add(_miTrayMonitorStatus);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Show", null, (s, e) => RestoreFromTray());
            menu.Items.Add("Start monitoring", null, (s, e) => StartMonitor());
            menu.Items.Add("Stop monitoring", null, (s, e) => _monitor.Stop());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(_miTrayAutostart);
            menu.Items.Add(_miTrayAutoMonitor);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => { _reallyExit = true; Close(); });

            _tray = new Forms.NotifyIcon
            {
                Text = "SteamTV Monitor",
                Icon = _trayIconUnknown,
                Visible = true,
                ContextMenuStrip = menu
            };
            _tray.DoubleClick += (s, e) => RestoreFromTray();
            _tray.BalloonTipClicked += (s, e) =>
            {
                if (_updateAvailable)
                    Process.Start(new ProcessStartInfo(UpdateChecker.ReleasesPageUrl) { UseShellExecute = true });
            };
        }

        private void HideToTray()
        {
            _tray.Visible = true;
            Hide();
            ShowInTaskbar = false;
        }

        private void RestoreFromTray()
        {
            Show();
            ShowInTaskbar = true;
            WindowState = WindowState.Normal;
            Activate();
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
            _monitor.Log -= AppendLog;
            _monitor.RunningChanged -= OnRunningChanged;
            _monitor.Notify -= ShowTrayBalloon;
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

        private void BtnActivateNow_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateIpOrWarn()) return;
            SaveUiToSettings();
            _monitor.ActivateNow();
        }

        private void BtnDeactivateNow_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateIpOrWarn()) return;
            SaveUiToSettings();
            _monitor.DeactivateNow();
        }
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
