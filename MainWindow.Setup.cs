using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace NekoCouch
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
            { LblHotkeys.Text = Loc.T("Hot.NeedMod"); return; }
            field.Text = new KeyGestureConverter().ConvertToInvariantString(new KeyGesture(key, Keyboard.Modifiers));
            LblHotkeys.Text = Loc.T("Hot.PressSave");
        }

        // Registered shortcuts never reach the window, so they couldn't be recorded (e.g. to swap
        // the two). Lift them while a field has focus; leaving restores the saved ones.
        private void Hotkey_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => _hotkeys?.Dispose();

        private void Hotkey_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (e.NewFocus == TxtActivateHotkey || e.NewFocus == TxtDeactivateHotkey) return;
            ApplySavedHotkeys();
        }

        // In "one shortcut for both" mode only the first one is registered; it toggles.
        private string[] ApplySavedHotkeys() =>
            _hotkeys?.ApplyEach(_settings.ActivateHotkey, _settings.HotkeyToggle ? "" : _settings.DeactivateHotkey) ?? new string[0];

        private void SaveHotkeys_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _hotkeys.Apply(TxtActivateHotkey.Text, _settings.HotkeyToggle ? "" : TxtDeactivateHotkey.Text);
                _settings.ActivateHotkey = TxtActivateHotkey.Text;
                _settings.DeactivateHotkey = TxtDeactivateHotkey.Text;
                _settings.Save();
                LblHotkeys.Text = Loc.T("Hot.Saved");
                UpdateHotkeyHints();
            }
            catch (Exception ex) { LblHotkeys.Text = ex.Message; AppendLog("Hotkeys: " + ex.Message); }
        }

        private void ChkHotkeyToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_loadingUi) return;
            _settings.HotkeyToggle = ChkHotkeyToggle.IsChecked == true;
            _settings.Save();
            UpdateHotkeyFields();
            foreach (var err in ApplySavedHotkeys()) AppendLog(err);
            UpdateHotkeyHints();
        }

        private void UpdateHotkeyFields()
        {
            bool one = _settings.HotkeyToggle;
            LblHotkeyOn.Text = Loc.T(one ? "Set.Hotkeys.Both" : "Set.Hotkeys.On");
            LblHotkeyOff.Text = Loc.T("Set.Hotkeys.Off");
            LblHotkeyOff.Visibility = TxtDeactivateHotkey.Visibility = one ? Visibility.Collapsed : Visibility.Visible;
        }

        // Button tooltips name the shortcut that does the same thing.
        private void UpdateHotkeyHints()
        {
            string on = _settings.ActivateHotkey, off = _settings.HotkeyToggle ? _settings.ActivateHotkey : _settings.DeactivateHotkey;
            BtnActivateNow.ToolTip = Loc.T("Home.Play.Tip") + (string.IsNullOrEmpty(on) ? "" : "\n" + Loc.T("Home.Tip.Key", on));
            BtnDeactivateNow.ToolTip = Loc.T("Home.Back.Tip") + (string.IsNullOrEmpty(off) ? "" : "\n" + Loc.T("Home.Tip.Key", off));
        }

        private async void DetectLauncher_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateIpOrWarn()) return;
            SaveUiToSettings();
            string ip = TxtIp.Text.Trim();
            var adb = new Adb(string.IsNullOrWhiteSpace(TxtAdbPath.Text) ? "adb.exe" : TxtAdbPath.Text.Trim(), ip);
            BtnDetectLauncher.IsEnabled = false;
            LblTvSetup.Text = Loc.T("Launcher.Looking");
            try
            {
                var component = await Task.Run(() => adb.DetectHomeComponent());
                if (TxtIp.Text.Trim() != ip) throw new InvalidOperationException(Loc.T("Launcher.IpChanged"));
                TxtTvHomeComponent.Text = component;
                _settings.TvHomeComponent = component;
                _settings.Save();
                LblTvSetup.Text = Loc.T("Launcher.Saved", component);
                AppendLog(LblTvSetup.Text);
            }
            catch (Exception ex) { LblTvSetup.Text = ex.Message; AppendLog("Launcher detection: " + ex.Message); }
            finally { BtnDetectLauncher.IsEnabled = true; }
        }
    }
}
