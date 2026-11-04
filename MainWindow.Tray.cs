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

        // The tray wants the small frame drawn for its size (just the play mark at 16–24 px), not
        // the 32 px TV scaled down into a blur.
        private static System.Drawing.Icon LoadTrayIcon()
        {
            try
            {
                using (var s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("SteamTV.start_icon.ico"))
                    if (s != null) return new System.Drawing.Icon(s, Forms.SystemInformation.SmallIconSize);
            }
            catch { }
            return LoadAppIcon();
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
                        int size = Math.Max(5, bmp.Width * 7 / 16);
                        var rect = new System.Drawing.Rectangle(bmp.Width - size, bmp.Height - size, size - 1, size - 1);
                        using (var ring = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0x0A, 0x0C, 0x18)))
                            g.FillEllipse(ring, System.Drawing.Rectangle.Inflate(rect, 1, 1));
                        using (var brush = new System.Drawing.SolidBrush(dotColor.Value))
                            g.FillEllipse(brush, rect);
                    }
                }
                return System.Drawing.Icon.FromHandle(bmp.GetHicon());
            }
        }

        private Forms.ToolStripMenuItem _miTrayPlay, _miTrayBack, _miTrayMonitoring, _miTrayShow, _miTrayLog, _miTrayExit;

        private void BuildTray()
        {
            var baseIcon = LoadTrayIcon();
            _trayIconUnknown = BuildStatusIcon(baseIcon, null);
            _trayIconOk = BuildStatusIcon(baseIcon, System.Drawing.Color.FromArgb(0x7E, 0xE8, 0xC4));
            _trayIconBad = BuildStatusIcon(baseIcon, System.Drawing.Color.FromArgb(0xFF, 0x8A, 0x8A));

            // Header: what SteamTV is doing right now (not clickable).
            _miTrayMonitorStatus = new Forms.ToolStripMenuItem { Enabled = false };
            _miTrayAdbStatus = new Forms.ToolStripMenuItem { Enabled = false };

            _miTrayPlay = new Forms.ToolStripMenuItem { Font = new Font(Forms.SystemInformation.MenuFont, System.Drawing.FontStyle.Bold) };
            _miTrayPlay.Click += (s, e) => ActivateNow(interactive: false);
            _miTrayBack = new Forms.ToolStripMenuItem();
            _miTrayBack.Click += (s, e) => DeactivateNow(interactive: false);

            _miTrayMonitoring = new Forms.ToolStripMenuItem();
            _miTrayMonitoring.Click += (s, e) => { if (_monitor.IsRunning) _monitor.Stop(); else StartMonitor(interactive: false); };
            _miTrayShow = new Forms.ToolStripMenuItem();
            _miTrayShow.Click += (s, e) => RestoreFromTray();

            _miTrayAutostart = new Forms.ToolStripMenuItem { CheckOnClick = true };
            // Synced with the Settings toggle — its Checked/Unchecked handler calls SetAutostart().
            _miTrayAutostart.Click += (s, e) => ChkAutostart.IsChecked = _miTrayAutostart.Checked;
            _miTrayAutoMonitor = new Forms.ToolStripMenuItem { CheckOnClick = true };
            _miTrayAutoMonitor.Click += (s, e) => ChkAutoMonitor.IsChecked = _miTrayAutoMonitor.Checked;

            _miTrayUpdate = new Forms.ToolStripMenuItem { Visible = false };
            _miTrayUpdate.Click += (s, e) => InstallUpdate();
            _miTrayLog = new Forms.ToolStripMenuItem();
            _miTrayLog.Click += (s, e) => OpenUrl(AppLog.FilePath);
            _miTrayExit = new Forms.ToolStripMenuItem();
            _miTrayExit.Click += (s, e) => { _reallyExit = true; Close(); };

            var menu = new Forms.ContextMenuStrip { Renderer = new Forms.ToolStripProfessionalRenderer(new TrayColors()), ShowImageMargin = false, ShowCheckMargin = true };
            menu.Opening += (s, e) => RefreshTrayTexts();
            menu.Items.AddRange(new Forms.ToolStripItem[]
            {
                _miTrayMonitorStatus, _miTrayAdbStatus, new Forms.ToolStripSeparator(),
                _miTrayPlay, _miTrayBack, new Forms.ToolStripSeparator(),
                _miTrayMonitoring, _miTrayShow, new Forms.ToolStripSeparator(),
                _miTrayAutostart, _miTrayAutoMonitor, new Forms.ToolStripSeparator(),
                _miTrayUpdate, _miTrayLog, _miTrayExit,
            });
            foreach (Forms.ToolStripItem item in menu.Items) item.ForeColor = System.Drawing.Color.FromArgb(0xEC, 0xEB, 0xF5);
            _miTrayMonitorStatus.ForeColor = _miTrayAdbStatus.ForeColor = System.Drawing.Color.FromArgb(0x8E, 0x93, 0xB4);

            _tray = new Forms.NotifyIcon
            {
                Text = "SteamTV",
                Icon = _trayIconUnknown,
                Visible = true,
                ContextMenuStrip = menu
            };
            _tray.DoubleClick += (s, e) => RestoreFromTray();
            _tray.BalloonTipClicked += (s, e) =>
            {
                if (_updateAvailable) InstallUpdate();
            };
            RefreshTrayTexts();
        }

        // Menu texts and check marks; also run when the language changes.
        private void RefreshTrayTexts()
        {
            if (_tray == null) return;
            _miTrayMonitorStatus.Text = "SteamTV — " + TrayStatusText();
            _miTrayAdbStatus.Text = Loc.T(_lastAdbReachable == true ? "Nav.TvOk" : _lastAdbReachable == false ? "Nav.TvBad" : "Nav.TvUnknown");
            _miTrayPlay.Text = "▶  " + Loc.T("Tray.Play");
            _miTrayPlay.Enabled = !_monitor.IsDisplayActive;
            _miTrayBack.Text = Loc.T("Tray.Back");
            _miTrayMonitoring.Text = Loc.T("Tray.Monitoring");
            _miTrayMonitoring.Checked = _monitor.IsRunning;
            _miTrayShow.Text = Loc.T("Tray.Show");
            _miTrayAutostart.Text = Loc.T("Tray.Autostart");
            _miTrayAutostart.Checked = IsAutostartEnabled();
            _miTrayAutoMonitor.Text = Loc.T("Tray.AutoMonitor");
            _miTrayAutoMonitor.Checked = _settings.AutoMonitorOnStart;
            if (_updateTag != null) _miTrayUpdate.Text = Loc.T("Tray.Update", _updateTag);
            _miTrayLog.Text = Loc.T("Tray.Log");
            _miTrayExit.Text = Loc.T("Tray.Exit");
            _tray.Text = BuildTrayTooltip();
        }

        // The tray menu in the app's night palette instead of the default light grey.
        private sealed class TrayColors : Forms.ProfessionalColorTable
        {
            private static readonly System.Drawing.Color Bg = System.Drawing.Color.FromArgb(0x16, 0x1A, 0x2E);
            private static readonly System.Drawing.Color Hover = System.Drawing.Color.FromArgb(0x26, 0x2B, 0x47);
            private static readonly System.Drawing.Color Line = System.Drawing.Color.FromArgb(0x35, 0x3B, 0x5E);
            public override System.Drawing.Color ToolStripDropDownBackground => Bg;
            public override System.Drawing.Color MenuBorder => Line;
            public override System.Drawing.Color MenuItemBorder => Hover;
            public override System.Drawing.Color MenuItemSelected => Hover;
            public override System.Drawing.Color MenuItemSelectedGradientBegin => Hover;
            public override System.Drawing.Color MenuItemSelectedGradientEnd => Hover;
            public override System.Drawing.Color ImageMarginGradientBegin => Bg;
            public override System.Drawing.Color ImageMarginGradientMiddle => Bg;
            public override System.Drawing.Color ImageMarginGradientEnd => Bg;
            public override System.Drawing.Color SeparatorDark => Line;
            public override System.Drawing.Color SeparatorLight => Bg;
            public override System.Drawing.Color CheckBackground => System.Drawing.Color.FromArgb(0x5A, 0x2D, 0x48);
            public override System.Drawing.Color CheckSelectedBackground => System.Drawing.Color.FromArgb(0x7A, 0x3A, 0x60);
            public override System.Drawing.Color CheckPressedBackground => System.Drawing.Color.FromArgb(0x7A, 0x3A, 0x60);
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
