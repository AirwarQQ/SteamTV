// Main window: tab-based UI — Monitor settings, Gamepad management, Test actions.
using System;
using System.Drawing;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SteamTV
{

    internal sealed partial class MainForm : Form
    {
        private readonly AppSettings _settings;
        private readonly MonitorService _monitor;

        // Monitor tab
        private TextBox _txtIp;
        private NumericUpDown _numHdmiPort;
        private NumericUpDown _numTarget;
        private Label _lblAdb;
        private CheckBox _chkDisableOthers;
        private CheckBox _chkWakeTV;
        private CheckBox _chkSourceSwitch;
        private CheckBox _chkBigPicture;
        private CheckBox _chkAutostart;
        private Label _lblStatus;
        private Button _btnStart, _btnStop, _btnExit;

        // Gamepads tab
        private ListBox _lstWatched;
        private ListBox _lstConnected;
        private Label _lblNoGamepads;

        // Shared
        private TabControl _tabs;
        private TextBox _txtLog;
        private NotifyIcon _tray;
        private ToolStripMenuItem _miTrayAutostart;
        private ToolStripMenuItem _miTrayAutoMonitor;
        private System.Windows.Forms.Timer _statusTimer;
        private bool _reallyExit;

        // ADB periodic check state
        private DateTime _lastAdbCheck = DateTime.MinValue;
        private bool _adbCheckPending;

        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "SteamTV_Controller";

        public MainForm(bool autostart)
        {
            _settings = AppSettings.Load();
            _monitor = new MonitorService(_settings);
            _monitor.Log += AppendLog;
            _monitor.RunningChanged += OnRunningChanged;

            BuildUi();
            LoadSettingsToUi();

            _statusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _statusTimer.Tick += (s, e) => OnStatusTick();
            _statusTimer.Start();

            if (autostart)
                Shown += (s, e) => { HideToTray(); if (_settings.AutoMonitorOnStart) StartMonitor(); };
        }

        // -------------------------------------------------------------------------
        // UI construction
        // -------------------------------------------------------------------------

        private void BuildUi()
        {
            SuspendLayout();
            Text = "SteamTV Monitor";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(492, 556);
            Font = new Font("Segoe UI", 9f);
            Icon = LoadAppIcon();

            _tabs = new TabControl { Left = 0, Top = 0, Width = 492, Height = 316 };
            _tabs.TabPages.Add(BuildMonitorTab());
            _tabs.TabPages.Add(BuildGamepadsTab());
            _tabs.TabPages.Add(BuildTestTab());
            _tabs.SelectedIndexChanged += (s, e) =>
            {
                if (_tabs.SelectedIndex == 1) RefreshConnectedList();
            };

            // Status + buttons live on the main form (outside tabs) so they're always visible
            _lblStatus = new Label
            {
                Left = 8, Top = 322, AutoSize = true,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };

            _btnStart = new Button { Text = "Start", Left = 8, Top = 346, Width = 100, Height = 28 };
            _btnStart.Click += (s, e) => StartMonitor();

            _btnStop = new Button { Text = "Stop", Left = 116, Top = 346, Width = 100, Height = 28 };
            _btnStop.Click += (s, e) => _monitor.Stop();

            _btnExit = new Button { Text = "Exit", Left = 374, Top = 346, Width = 110, Height = 28 };
            _btnExit.Click += (s, e) => { _reallyExit = true; Close(); };

            var lblLog = new Label { Text = "Log:", Left = 8, Top = 382, AutoSize = true };

            _txtLog = new TextBox
            {
                Left = 8, Top = 400, Width = 476, Height = 148,
                Multiline = true, ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(250, 250, 250),
                Font = new Font("Consolas", 8.5f)
            };

            BuildTray();

            Controls.AddRange(new Control[]
            {
                _tabs, _lblStatus, _btnStart, _btnStop, _btnExit, lblLog, _txtLog
            });

            // Must be set AFTER controls are added so PerformAutoScale() fires with a valid HWND.
            // PerMonitorV2 (declared in app.manifest) makes WinForms call GetDpiForWindow(Handle)
            // which returns the actual per-monitor DPI, not the system default.
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;

            UpdateStatusLabel();

            FormClosing += OnFormClosing;
            Resize += (s, e) => { if (WindowState == FormWindowState.Minimized) HideToTray(); };

            ResumeLayout(false);
            PerformLayout();
        }

        private TabPage BuildMonitorTab()
        {
            var tab = new TabPage("Monitor");
            var p = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };

            // --- Connection group ---
            var grpConn = new GroupBox { Text = "Connection", Left = 8, Top = 4, Width = 460, Height = 86 };

            var lblIp = new Label { Text = "TV IP:", Left = 8, Top = 22, AutoSize = true };
            _txtIp = new TextBox { Left = 80, Top = 19, Width = 210 };

            _lblAdb = new Label
            {
                Text = "○ ADB  ↺15s",
                Left = 295, Top = 22, AutoSize = true,
                ForeColor = Color.Gray,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Cursor = Cursors.Help
            };
            var ttAdb = new ToolTip();
            ttAdb.SetToolTip(_lblAdb, "ADB reachability check — runs in background every 15 s.\n● green = reachable   ● orange = not reachable");

            var lblHdmi = new Label { Text = "HDMI port:", Left = 8, Top = 54, AutoSize = true };
            _numHdmiPort = new NumericUpDown { Left = 80, Top = 51, Width = 48, Minimum = 1, Maximum = 4 };
            var lblHdmiHint = new Label
            {
                Text = "(1–4, used by the TV External Source app)",
                Left = 136, Top = 54, AutoSize = true, ForeColor = Color.Gray
            };

            grpConn.Controls.AddRange(new Control[]
                { lblIp, _txtIp, _lblAdb, lblHdmi, _numHdmiPort, lblHdmiHint });

            // --- Display group ---
            var grpDisp = new GroupBox { Text = "Display", Left = 8, Top = 96, Width = 460, Height = 52 };

            var lblTarget = new Label { Text = "Target display #:", Left = 8, Top = 22, AutoSize = true };
            _numTarget = new NumericUpDown { Left = 120, Top = 19, Width = 52, Minimum = 1, Maximum = 64, Value = 3 };
            var btnDisplays = new Button { Text = "Displays…", Left = 182, Top = 18, Width = 88, Height = 24 };
            btnDisplays.Click += (s, e) => ShowDisplaysList();

            grpDisp.Controls.AddRange(new Control[] { lblTarget, _numTarget, btnDisplays });

            // --- Features group ---
            // Height=130 fits 5 checkboxes at 22 px spacing within the ~291 px tab content area
            var grpFeat = new GroupBox { Text = "Features", Left = 8, Top = 154, Width = 460, Height = 130 };

            _chkDisableOthers = Chk("Disable other monitors when TV is active", 8, 18);
            _chkWakeTV = Chk("Wake TV via ADB on gamepad connect", 8, 40);
            _chkSourceSwitch = Chk("Switch HDMI source on TV", 8, 62);
            _chkBigPicture = Chk("Launch Steam Big Picture", 8, 84);
            _chkAutostart = Chk("Run at Windows startup (hidden in tray)", 8, 106);
            _chkAutostart.CheckedChanged += (s, e) => SetAutostart(_chkAutostart.Checked);

            grpFeat.Controls.AddRange(new Control[]
                { _chkDisableOthers, _chkWakeTV, _chkSourceSwitch, _chkBigPicture, _chkAutostart });

            p.Controls.AddRange(new Control[] { grpConn, grpDisp, grpFeat });
            tab.Controls.Add(p);
            return tab;
        }

        private TabPage BuildGamepadsTab()
        {
            var tab = new TabPage("Gamepads");
            var p = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };

            // Watched list
            var grpWatched = new GroupBox
            {
                Text = "Watched — trigger SteamTV when any of these connects",
                Left = 8, Top = 4, Width = 460, Height = 138
            };

            _lstWatched = new ListBox { Left = 8, Top = 18, Width = 360, Height = 82 };

            var btnRemove = new Button { Text = "Remove", Left = 376, Top = 18, Width = 76, Height = 28 };
            btnRemove.Click += (s, e) => RemoveWatchedGamepad();

            _lblNoGamepads = new Label
            {
                Text = "⚠  No gamepads configured — add at least one to enable the trigger.",
                Left = 8, Top = 106, Width = 444, Height = 22,
                ForeColor = Color.OrangeRed
            };

            grpWatched.Controls.AddRange(new Control[] { _lstWatched, btnRemove, _lblNoGamepads });

            // Connected list
            var grpConnected = new GroupBox
            {
                Text = "Connected right now",
                Left = 8, Top = 148, Width = 460, Height = 122
            };

            _lstConnected = new ListBox { Left = 8, Top = 18, Width = 360, Height = 68 };

            var btnRefresh = new Button { Text = "Refresh", Left = 376, Top = 18, Width = 76, Height = 28 };
            btnRefresh.Click += (s, e) => RefreshConnectedList();

            var btnAdd = new Button { Text = "↑ Add to Watched", Left = 8, Top = 92, Width = 160, Height = 24 };
            btnAdd.Click += (s, e) => AddToWatched();

            grpConnected.Controls.AddRange(new Control[] { _lstConnected, btnRefresh, btnAdd });

            p.Controls.AddRange(new Control[] { grpWatched, grpConnected });
            tab.Controls.Add(p);
            return tab;
        }

        private TabPage BuildTestTab()
        {
            var tab = new TabPage("Test");
            var p = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };

            var grp = new GroupBox { Text = "Test individual actions", Left = 8, Top = 4, Width = 460, Height = 258 };

            int x1 = 10, x2 = 238, bw = 210, bh = 30, yStep = 38;
            int y = 22;

            grp.Controls.Add(TestBtn("Wake TV", x1, y, bw, bh,
                () => new Adb(_settings.AdbPath, _settings.TvIp).WakeTv()));
            grp.Controls.Add(TestBtn("Switch HDMI Source", x2, y, bw, bh, () =>
            {
                var adb = new Adb(_settings.AdbPath, _settings.TvIp);
                adb.SwitchSourceToPc(_settings.HdmiSourcePackage);
                return "done (port " + _settings.HdmiPort + ")";
            }));
            y += yStep;

            grp.Controls.Add(TestBtn("Check ADB Connection", x1, y, bw, bh, () =>
            {
                bool ok = new Adb(_settings.AdbPath, _settings.TvIp).IsAdbReachable(5000);
                return ok ? "reachable ✓" : "not reachable ✗";
            }));
            grp.Controls.Add(TestBtn("Restore TV Home Screen", x2, y, bw, bh, () =>
            {
                new Adb(_settings.AdbPath, _settings.TvIp).RestoreSourceBeforeShutdown(_settings.TvHomeComponent);
                return "done";
            }));
            y += yStep;

            grp.Controls.Add(TestBtn("Start Big Picture", x1, y, bw, bh, () =>
            {
                SteamHelper.StartBigPicture(_settings.SteamPath);
                return "launched";
            }));
            grp.Controls.Add(TestBtn("List Displays", x2, y, bw, bh, () =>
            {
                var list = DisplayManager.ListDisplays();
                if (list.Count == 0) return "none found";
                var sb = new StringBuilder();
                foreach (var d in list)
                    sb.Append("#" + d.Number + " " + (d.Active ? "[on]" : "[off]") + " " + d.FriendlyName + " | ");
                return sb.ToString().TrimEnd(' ', '|');
            }));
            y += yStep;

            grp.Controls.Add(TestBtn("Enable Display #N", x1, y, bw, bh, () =>
            {
                string err;
                bool ok = DisplayManager.EnableDisplay(_settings.TargetDisplay, out err);
                return ok ? "enabled #" + _settings.TargetDisplay : err;
            }));
            grp.Controls.Add(TestBtn("Disable Display #N", x2, y, bw, bh, () =>
            {
                string err;
                bool ok = DisplayManager.DisableDisplay(_settings.TargetDisplay, out err);
                return ok ? "disabled #" + _settings.TargetDisplay : err;
            }));
            y += yStep;

            grp.Controls.Add(TestBtn("Restore All Displays", x1, y, bw, bh, () =>
            {
                string snap_err;
                var snap = DisplayManager.QueryAll(out snap_err);
                if (snap == null) return "query failed: " + snap_err;
                string err;
                bool ok = DisplayManager.RestoreAll(snap, out err);
                return ok ? "restored" : err;
            }));
            grp.Controls.Add(TestBtn("Minimize Steam", x2, y, bw, bh, () =>
            {
                SteamHelper.MinimizeSteamWindow();
                return "done";
            }));
            y += yStep;

            grp.Controls.Add(TestBtn("Set Highest Refresh Rate", x1, y, bw, bh, () =>
            {
                string err;
                bool ok = DisplayManager.SetHighestRefreshRate(_settings.TargetDisplay, out err, AppendLog);
                return ok ? "applied to #" + _settings.TargetDisplay : err;
            }));

            p.Controls.Add(grp);

            var lblNote = new Label
            {
                Text = "Each action runs in the background and logs the result below. " +
                       "Settings are saved before each test.",
                Left = 8, Top = 268, Width = 460, Height = 32,
                ForeColor = Color.Gray
            };
            p.Controls.Add(lblNote);

            tab.Controls.Add(p);
            return tab;
        }

        // -------------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------------

        private static CheckBox Chk(string text, int x, int y)
            => new CheckBox { Text = text, Left = x, Top = y, AutoSize = true };

        private Button TestBtn(string label, int x, int y, int w, int h, Func<string> action)
        {
            var btn = new Button { Text = label, Left = x, Top = y, Width = w, Height = h };
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
            _txtIp.Text = _settings.TvIp;
            _numHdmiPort.Value = Math.Max(1, Math.Min(4, _settings.HdmiPort));
            _numTarget.Value = Math.Max(_numTarget.Minimum, Math.Min(_numTarget.Maximum, _settings.TargetDisplay));
            _chkDisableOthers.Checked = _settings.DisableOthers;
            _chkWakeTV.Checked = _settings.EnableWakeTV;
            _chkSourceSwitch.Checked = _settings.EnableSourceSwitch;
            _chkBigPicture.Checked = _settings.EnableBigPicture;
            _chkAutostart.Checked = IsAutostartEnabled();
            RefreshWatchedList();
        }

        private void SaveUiToSettings()
        {
            _settings.TvIp = _txtIp.Text.Trim();
            _settings.HdmiPort = (int)_numHdmiPort.Value;
            _settings.TargetDisplay = (int)_numTarget.Value;
            _settings.DisableOthers = _chkDisableOthers.Checked;
            _settings.EnableWakeTV = _chkWakeTV.Checked;
            _settings.EnableSourceSwitch = _chkSourceSwitch.Checked;
            _settings.EnableBigPicture = _chkBigPicture.Checked;
            _settings.Save();
        }

        private void StartMonitor()
        {
            if (string.IsNullOrWhiteSpace(_txtIp.Text))
            {
                MessageBox.Show("Enter the TV IP address.", "SteamTV", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            System.Net.IPAddress addr;
            if (!System.Net.IPAddress.TryParse(_txtIp.Text.Trim(), out addr))
            {
                MessageBox.Show("Invalid IP address format.", "SteamTV", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            SaveUiToSettings();
            _monitor.Start();
        }

        // -------------------------------------------------------------------------
        // Gamepad management
        // -------------------------------------------------------------------------

        private void RefreshWatchedList()
        {
            _lstWatched.Items.Clear();
            foreach (var g in _settings.WatchedGamepads)
                _lstWatched.Items.Add(g);
            _lblNoGamepads.Visible = _settings.WatchedGamepads.Count == 0;
        }

        private void RefreshConnectedList()
        {
            _lstConnected.Items.Clear();
            _lstConnected.Items.Add("Scanning...");
            Task.Run(() =>
            {
                var pads = SteamHelper.EnumerateGamepads();
                BeginInvoke(new Action(() =>
                {
                    _lstConnected.Items.Clear();
                    if (pads.Count == 0)
                        _lstConnected.Items.Add("(no gamepad-class HID devices detected)");
                    else
                        foreach (var g in pads)
                            _lstConnected.Items.Add(g);
                }));
            });
        }

        private void AddToWatched()
        {
            if (_lstConnected.SelectedItem is ConnectedGamepad pad)
            {
                if (_settings.WatchedGamepads.Exists(w =>
                    string.Equals(w.HardwareId, pad.HardwareId, StringComparison.OrdinalIgnoreCase)))
                {
                    MessageBox.Show("This gamepad is already in the watched list.", "SteamTV");
                    return;
                }
                _settings.WatchedGamepads.Add(new GamepadEntry
                {
                    HardwareId = pad.HardwareId,
                    FriendlyName = pad.FriendlyName
                });
                _settings.Save();
                RefreshWatchedList();
            }
        }

        private void RemoveWatchedGamepad()
        {
            if (_lstWatched.SelectedItem is GamepadEntry entry)
            {
                _settings.WatchedGamepads.Remove(entry);
                _settings.Save();
                RefreshWatchedList();
            }
        }

        // -------------------------------------------------------------------------
        // Display list popup
        // -------------------------------------------------------------------------

        private void ShowDisplaysList()
        {
            var list = DisplayManager.ListDisplays();
            if (list.Count == 0)
            {
                MessageBox.Show("Failed to retrieve display list.", "Displays");
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
            MessageBox.Show(sb.ToString(), "Displays");
        }

        // -------------------------------------------------------------------------
        // Status + ADB check
        // -------------------------------------------------------------------------

        private void OnRunningChanged(bool running)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => OnRunningChanged(running))); return; }
            UpdateStatusLabel();
            bool canEdit = !running;
            _txtIp.Enabled = _numHdmiPort.Enabled = _numTarget.Enabled = canEdit;
            _chkDisableOthers.Enabled = _chkWakeTV.Enabled = _chkSourceSwitch.Enabled = canEdit;
            _chkBigPicture.Enabled = canEdit;
            _btnStart.Enabled = canEdit;
            _btnStop.Enabled = running;
        }

        private void OnStatusTick()
        {
            UpdateStatusLabel();

            // ADB check: run every 15 s in a background task
            if (!_adbCheckPending && (DateTime.Now - _lastAdbCheck).TotalSeconds >= 15)
            {
                System.Net.IPAddress addr;
                if (!System.Net.IPAddress.TryParse(_settings.TvIp ?? "", out addr))
                {
                    _lastAdbCheck = DateTime.Now;
                    _lblAdb.Text = "● ADB  invalid IP";
                    _lblAdb.ForeColor = Color.OrangeRed;
                }
                else
                {
                    _adbCheckPending = true;
                    string ip = _settings.TvIp;
                    string adbPath = _settings.AdbPath;
                    Task.Run(() =>
                    {
                        bool ok = new Adb(adbPath, ip).IsAdbReachable(4000);
                        BeginInvoke(new Action(() =>
                        {
                            _adbCheckPending = false;
                            _lastAdbCheck = DateTime.Now;
                            _lblAdb.Text = "● ADB  ↺15s";
                            _lblAdb.ForeColor = ok ? Color.Green : Color.OrangeRed;
                        }));
                    });
                }
            }
        }

        private void UpdateStatusLabel()
        {
            bool running = _monitor.IsRunning;
            _lblStatus.Text = running ? "● Running" : "● Stopped";
            _lblStatus.ForeColor = running ? Color.Green : Color.OrangeRed;
            if (_tray != null)
                _tray.Text = "SteamTV — " + (running ? "running" : "stopped");
        }

        // -------------------------------------------------------------------------
        // Log
        // -------------------------------------------------------------------------

        private void AppendLog(string msg)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => AppendLog(msg))); return; }
            _txtLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + Environment.NewLine);
            if (_txtLog.TextLength > 20000)
                _txtLog.Text = _txtLog.Text.Substring(_txtLog.TextLength - 12000);
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
                        k.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\" -autostart");
                    else
                        k.DeleteValue(RunValueName, false);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to change autostart: " + ex.Message, "SteamTV",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // -------------------------------------------------------------------------
        // Tray icon
        // -------------------------------------------------------------------------

        private void BuildTray()
        {
            _miTrayAutostart = new ToolStripMenuItem("Autostart app") { CheckOnClick = true };
            _miTrayAutostart.Click += (s, e) =>
            {
                // Sync with Monitor tab checkbox — its CheckedChanged calls SetAutostart()
                _chkAutostart.Checked = _miTrayAutostart.Checked;
            };

            _miTrayAutoMonitor = new ToolStripMenuItem("Auto-start monitoring on launch") { CheckOnClick = true };
            _miTrayAutoMonitor.Click += (s, e) =>
            {
                _settings.AutoMonitorOnStart = _miTrayAutoMonitor.Checked;
                _settings.Save();
            };

            var menu = new ContextMenuStrip();
            menu.Opening += (s, e) =>
            {
                // Refresh checked state each time the menu opens
                _miTrayAutostart.Checked = IsAutostartEnabled();
                _miTrayAutoMonitor.Checked = _settings.AutoMonitorOnStart;
            };
            menu.Items.Add("Show", null, (s, e) => RestoreFromTray());
            menu.Items.Add("Start monitoring", null, (s, e) => StartMonitor());
            menu.Items.Add("Stop monitoring", null, (s, e) => _monitor.Stop());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_miTrayAutostart);
            menu.Items.Add(_miTrayAutoMonitor);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => { _reallyExit = true; Close(); });

            _tray = new NotifyIcon
            {
                Text = "SteamTV Monitor",
                Icon = LoadAppIcon(),
                Visible = true,
                ContextMenuStrip = menu
            };
            _tray.DoubleClick += (s, e) => RestoreFromTray();
        }

        // -------------------------------------------------------------------------
        // DPI scaling
        // -------------------------------------------------------------------------

        private static Icon LoadAppIcon()
        {
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { return SystemIcons.Application; }
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
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && !_reallyExit)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            _statusTimer?.Stop();
            _monitor.Log -= AppendLog;
            _monitor.RunningChanged -= OnRunningChanged;
            _monitor.Stop();
            if (_tray != null) _tray.Visible = false;
        }
    }

}
