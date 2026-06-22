// Main window (WinForms): IP field, target display, checkboxes, log, tray icon.
using System;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SteamTV
{

    internal sealed partial class MainForm : Form
    {
        private readonly AppSettings _settings;
        private readonly MonitorService _monitor;

        private TextBox _txtIp;
        private NumericUpDown _numTarget;
        private CheckBox _chkDisableOthers;
        private CheckBox _chkAutostart;
        private Button _btnStart, _btnStop, _btnDisplays, _btnExit;
        private Label _lblStatus;
        private TextBox _txtLog;
        private NotifyIcon _tray;
        private System.Windows.Forms.Timer _statusTimer;
        private bool _reallyExit;

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
            _statusTimer.Tick += (s, e) => UpdateStatusLabel();
            _statusTimer.Start();

            if (autostart)
            {
                // started from autostart: hide to tray and start immediately
                Shown += (s, e) => { HideToTray(); StartMonitor(); };
            }
        }

        private void BuildUi()
        {
            Text = "SteamTV Monitor";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(380, 360);
            Font = new Font("Segoe UI", 9f);

            var lblIp = new Label { Text = "TV IP:", Location = new Point(15, 18), AutoSize = true };
            _txtIp = new TextBox { Location = new Point(140, 15), Width = 220 };

            var lblTarget = new Label { Text = "Target Display #:", Location = new Point(15, 50), AutoSize = true };
            _numTarget = new NumericUpDown { Location = new Point(140, 47), Width = 60, Minimum = 1, Maximum = 64, Value = 3 };
            _btnDisplays = new Button { Text = "Displays…", Location = new Point(210, 46), Width = 90 };
            _btnDisplays.Click += (s, e) => ShowDisplays();

            _chkDisableOthers = new CheckBox
            {
                Text = "Disable other monitors (except target)",
                Location = new Point(15, 82),
                AutoSize = true
            };

            _chkAutostart = new CheckBox
            {
                Text = "Auto-start with Windows (background)",
                Location = new Point(15, 108),
                AutoSize = true
            };
            _chkAutostart.CheckedChanged += (s, e) => SetAutostart(_chkAutostart.Checked);

            _lblStatus = new Label
            {
                Location = new Point(15, 140),
                AutoSize = true,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };

            _btnStart = new Button { Text = "Start", Location = new Point(15, 165), Width = 110, Height = 32 };
            _btnStart.Click += (s, e) => StartMonitor();

            _btnStop = new Button { Text = "Stop", Location = new Point(135, 165), Width = 110, Height = 32 };
            _btnStop.Click += (s, e) => _monitor.Stop();

            _btnExit = new Button { Text = "Exit", Location = new Point(255, 165), Width = 105, Height = 32 };
            _btnExit.Click += (s, e) => { _reallyExit = true; Close(); };

            var lblLog = new Label { Text = "Log:", Location = new Point(15, 205), AutoSize = true };
            _txtLog = new TextBox
            {
                Location = new Point(15, 225),
                Size = new Size(345, 120),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.White
            };

            Controls.AddRange(new Control[]
            {
                lblIp, _txtIp, lblTarget, _numTarget, _btnDisplays,
                _chkDisableOthers, _chkAutostart, _lblStatus,
                _btnStart, _btnStop, _btnExit, lblLog, _txtLog
            });

            // system tray — so the monitor can run in background without a visible window
            var menu = new ContextMenuStrip();
            menu.Items.Add("Show", null, (s, e) => RestoreFromTray());
            menu.Items.Add("Start", null, (s, e) => StartMonitor());
            menu.Items.Add("Stop", null, (s, e) => _monitor.Stop());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => { _reallyExit = true; Close(); });

            _tray = new NotifyIcon
            {
                Text = "SteamTV Monitor",
                Icon = SystemIcons.Application,
                Visible = false,
                ContextMenuStrip = menu
            };
            _tray.DoubleClick += (s, e) => RestoreFromTray();

            FormClosing += OnFormClosing;
            Resize += (s, e) => { if (WindowState == FormWindowState.Minimized) HideToTray(); };

            UpdateStatusLabel();
        }

        private void LoadSettingsToUi()
        {
            _txtIp.Text = _settings.TvIp;
            _numTarget.Value = Math.Max(_numTarget.Minimum, Math.Min(_numTarget.Maximum, _settings.TargetDisplay));
            _chkDisableOthers.Checked = _settings.DisableOthers;
            _chkAutostart.Checked = IsAutostartEnabled();
        }

        private void SaveUiToSettings()
        {
            _settings.TvIp = _txtIp.Text.Trim();
            _settings.TargetDisplay = (int)_numTarget.Value;
            _settings.DisableOthers = _chkDisableOthers.Checked;
            _settings.Save();
        }

        private void StartMonitor()
        {
            if (string.IsNullOrWhiteSpace(_txtIp.Text))
            {
                MessageBox.Show("Enter the TV IP address.", "SteamTV", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            SaveUiToSettings();
            _monitor.Start();
        }

        private void OnRunningChanged(bool running)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => OnRunningChanged(running))); return; }
            UpdateStatusLabel();
            // lock settings editing while running
            _txtIp.Enabled = _numTarget.Enabled = _chkDisableOthers.Enabled = !running;
            _btnStart.Enabled = !running;
            _btnStop.Enabled = running;
        }

        private void UpdateStatusLabel()
        {
            if (_monitor.IsRunning)
            {
                _lblStatus.Text = "Status: Running";
                _lblStatus.ForeColor = Color.Green;
            }
            else
            {
                _lblStatus.Text = "Status: Stopped";
                _lblStatus.ForeColor = Color.Red;
            }
            if (_tray != null) _tray.Text = "SteamTV — " + (_monitor.IsRunning ? "running" : "stopped");
        }

        private void AppendLog(string msg)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => AppendLog(msg))); return; }
            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + Environment.NewLine;
            _txtLog.AppendText(line);
            // keep the log from growing too large
            if (_txtLog.TextLength > 20000)
                _txtLog.Text = _txtLog.Text.Substring(_txtLog.TextLength - 12000);
        }

        private void ShowDisplays()
        {
            var list = DisplayManager.ListDisplays();
            if (list.Count == 0)
            {
                MessageBox.Show("Failed to retrieve display list.", "Displays");
                return;
            }
            var sb = new StringBuilder();
            sb.AppendLine("Detected displays (number — as shown in \"Screen Settings\"):");
            sb.AppendLine();
            foreach (var d in list)
                sb.AppendLine("#" + d.Number + "  " + (d.Active ? "[active]   " : "[disabled]  ") +
                              (string.IsNullOrEmpty(d.FriendlyName) ? "(name unavailable)" : d.FriendlyName));
            sb.AppendLine();
            sb.AppendLine("Enter the TV display number in the \"Target Display #\" field.");
            MessageBox.Show(sb.ToString(), "Displays");
        }

        // --- autostart ---
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

        // --- tray / closing ---
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
            _tray.Visible = false;
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            // Close button / Alt+F4 — minimize to tray, monitor keeps running.
            if (e.CloseReason == CloseReason.UserClosing && !_reallyExit)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            // Real exit
            _statusTimer?.Stop();
            _monitor.Stop();
            if (_tray != null) _tray.Visible = false;
        }
    }

}