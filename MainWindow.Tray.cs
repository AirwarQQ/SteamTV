// Main window: autostart and the tray icon.
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
            _miTrayUpdate = new Forms.ToolStripMenuItem("Install update") { Visible = false };
            _miTrayUpdate.Click += (s, e) => InstallUpdate();
            menu.Items.Add(_miTrayUpdate);
            menu.Items.Add("Open log file", null, (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo(AppLog.FilePath) { UseShellExecute = true }); } catch { }
            });
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
                if (_updateAvailable) InstallUpdate();
            };
        }

        private void HideToTray()
        {
            _tray.Visible = true;
            Hide();
            ShowInTaskbar = false;
        }

        // Another SteamTV.exe was started by hand while this one runs.
        internal void ShowFromTray() => RestoreFromTray();

        private void RestoreFromTray()
        {
            Show();
            ShowInTaskbar = true;
            WindowState = WindowState.Normal;
            Activate();
        }
    }

}
