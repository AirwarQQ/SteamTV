using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SteamTV
{
    public partial class MainWindow
    {
        private GlobalHotkeys _hotkeys;

        private void Hotkey_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Tab) return;
            e.Handled = true;
            var field = (TextBox)sender;
            if (key == Key.Escape) { field.Text = ""; return; }
            if (key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt ||
                key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin) return;
            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0)
            { LblHotkeys.Text = "Include Ctrl, Alt or Win in the shortcut."; return; }
            field.Text = new KeyGestureConverter().ConvertToInvariantString(new KeyGesture(key, Keyboard.Modifiers));
            LblHotkeys.Text = "Press Save shortcuts to apply.";
        }

        // Registered shortcuts never reach the window, so they couldn't be recorded (e.g. to swap
        // the two). Lift them while a field has focus; leaving restores the saved ones.
        private void Hotkey_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => _hotkeys?.Dispose();

        private void Hotkey_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (e.NewFocus == TxtActivateHotkey || e.NewFocus == TxtDeactivateHotkey) return;
            _hotkeys?.ApplyEach(_settings.ActivateHotkey, _settings.DeactivateHotkey);
        }

        private void SaveHotkeys_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _hotkeys.Apply(TxtActivateHotkey.Text, TxtDeactivateHotkey.Text);
                _settings.ActivateHotkey = TxtActivateHotkey.Text;
                _settings.DeactivateHotkey = TxtDeactivateHotkey.Text;
                _settings.Save();
                LblHotkeys.Text = "Shortcuts saved.";
            }
            catch (Exception ex) { LblHotkeys.Text = ex.Message; AppendLog("Hotkeys: " + ex.Message); }
        }

        private async void DetectLauncher_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateIpOrWarn()) return;
            SaveUiToSettings();
            string ip = TxtIp.Text.Trim();
            var adb = new Adb(string.IsNullOrWhiteSpace(TxtAdbPath.Text) ? "adb.exe" : TxtAdbPath.Text.Trim(), ip);
            BtnDetectLauncher.IsEnabled = false;
            LblTvSetup.Text = "Looking for the TV's default Home app…";
            try
            {
                var component = await Task.Run(() => adb.DetectHomeComponent());
                if (TxtIp.Text.Trim() != ip) throw new InvalidOperationException("TV address changed. Search again for the selected TV.");
                TxtTvHomeComponent.Text = component;
                _settings.TvHomeComponent = component;
                _settings.Save();
                LblTvSetup.Text = "Launcher saved: " + component;
                AppendLog(LblTvSetup.Text);
            }
            catch (Exception ex) { LblTvSetup.Text = ex.Message; AppendLog("Launcher detection: " + ex.Message); }
            finally { BtnDetectLauncher.IsEnabled = true; }
        }
    }
}
