// Main window: Devices tab, device-change notifications, display list.
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
            _hotkeys = new GlobalHotkeys(hwnd);
            var errors = _hotkeys.ApplyEach(_settings.ActivateHotkey, _settings.DeactivateHotkey);
            if (errors.Length > 0)
            {
                LblHotkeys.Text = string.Join("\n", errors);
                foreach (var err in errors) AppendLog(err);
                ShowTrayBalloon("SteamTV", string.Join("\n", errors) + "\nChange it on the Advanced tab.");
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x0312 && (wParam.ToInt32() == GlobalHotkeys.ActivateId || wParam.ToInt32() == GlobalHotkeys.DeactivateId))
            {
                handled = true;
                bool activate = wParam.ToInt32() == GlobalHotkeys.ActivateId;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (TxtActivateHotkey.IsKeyboardFocused || TxtDeactivateHotkey.IsKeyboardFocused) return;
                    AppendLog("Hotkey: " + (activate ? "Activate now" : "Deactivate / Restore"));
                    if (activate) ActivateNow(false); else DeactivateNow(false);
                }));
            }
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
    }

}
