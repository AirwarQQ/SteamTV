// Main window: Home Assistant tab (MQTT broker, PC power buttons).
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
        // Home Assistant (MQTT)
        // -------------------------------------------------------------------------

        // Runs exactly what the matching button does, on the UI thread, so Start/Stop and the
        // status line update the same way as from a click.
        private void OnHaCommand(HaSwitch sw, bool on)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => OnHaCommand(sw, on))); return; }
            if (sw == HaSwitch.Monitoring)
            {
                if (on) StartMonitor(interactive: false);
                else _monitor.Stop();
            }
            else
            {
                if (on) ActivateNow(interactive: false);
                else DeactivateNow(interactive: false);
            }
            UpdateStatusLabel();
        }

        private void OnHaPowerCommand(HaPower p)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => OnHaPowerCommand(p))); return; }
            if (p == HaPower.Sleep)
            {
                // A moment for the MQTT acknowledgement and the log line to go out before the PC freezes.
                AppendLog("Home Assistant: going to sleep.");
                Task.Delay(1500).ContinueWith(_ =>
                {
                    string err = PowerControl.Sleep();
                    if (err != null) AppendLog("Home Assistant: sleep FAILED — " + err);
                });
                return;
            }
            string what = p == HaPower.Shutdown ? "shutting down" : "restarting";
            string error = p == HaPower.Shutdown ? PowerControl.Shutdown() : PowerControl.Restart();
            if (error != null) { AppendLog("Home Assistant: " + what + " FAILED — " + error); return; }
            AppendLog("Home Assistant: " + what + " in " + PowerControl.ShutdownDelaySec + " s (cancel: shutdown /a).");
            ShowTrayBalloon("SteamTV", "Home Assistant: " + what + " in " + PowerControl.ShutdownDelaySec + " s. Run \"shutdown /a\" to cancel.");
        }

        private void UpdateMqttPowerInfo()
        {
            LblMqttPowerInfo.Text = ChkMqttPower.IsChecked == true
                ? Loc.T("Ha.Power.On", PowerControl.ShutdownDelaySec)
                : Loc.T("Ha.Power.Off");
        }

        private void ChkMqttPower_Changed(object sender, RoutedEventArgs e)
        {
            UpdateMqttPowerInfo();
            if (_loadingUi) return;
            _settings.MqttPowerButtons = ChkMqttPower.IsChecked == true;
            _settings.Save();
            // Reconnect re-announces discovery, which adds or removes the buttons in HA.
            if (_settings.MqttEnabled) StartMqtt();
        }

        private void OnMqttStatus(string status)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => OnMqttStatus(status))); return; }
            LblMqttStatus.Text = "● " + status;
            LblMqttStatus.Foreground = _ha.IsConnected ? Mint : !_ha.IsStarted || !_settings.MqttEnabled ? Muted : Rose;
        }

        private void SaveMqttUiToSettings()
        {
            _settings.MqttEnabled = ChkMqttEnabled.IsChecked == true;
            _settings.MqttHost = TxtMqttHost.Text.Trim();
            _settings.MqttPort = (int)(NumMqttPort.Value ?? 1883);
            _settings.MqttUser = TxtMqttUser.Text.Trim();
            _settings.MqttPassword = PwdMqtt.Password;
            _settings.Save();
        }

        private void StartMqtt()
        {
            if (string.IsNullOrWhiteSpace(_settings.MqttHost))
            {
                _ha.Stop();
                OnMqttStatus(Loc.T("Ha.NeedHost"));
                return;
            }
            _ha.Start(MqttConfig.From(_settings));
        }

        private void ApplyMqtt()
        {
            SaveMqttUiToSettings();
            if (_settings.MqttEnabled) StartMqtt();
            else if (_ha.IsStarted) { _ha.Stop(); AppendLog("Home Assistant integration switched off."); }
        }

        private void ChkMqttEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (!_loadingUi) ApplyMqtt();
        }

        private void BtnMqttApply_Click(object sender, RoutedEventArgs e) => ApplyMqtt();

        private async void BtnMqttTest_Click(object sender, RoutedEventArgs e)
        {
            var cfg = new MqttConfig
            {
                Host = TxtMqttHost.Text.Trim(), Port = (int)(NumMqttPort.Value ?? 1883),
                User = TxtMqttUser.Text.Trim(), Password = PwdMqtt.Password, DeviceId = _settings.MqttDeviceId,
            };
            if (string.IsNullOrEmpty(cfg.Host)) { AppendLog("[Test] MQTT: enter the broker host first."); return; }

            BtnMqttTest.IsEnabled = false;
            AppendLog("[Test] MQTT: connecting to " + cfg.Host + ":" + cfg.Port + "...");
            string err = await HomeAssistantMqtt.TestAsync(cfg);
            AppendLog("[Test] MQTT → " + (err == null ? "connected and logged in ✓" : "FAILED: " + err));
            BtnMqttTest.IsEnabled = true;
        }
    }

}
