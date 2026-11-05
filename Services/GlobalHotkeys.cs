using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace NekoCouch
{
    internal sealed class GlobalHotkeys : IDisposable
    {
        public const int ActivateId = 0x5350, DeactivateId = 0x5351;
        private readonly IntPtr _window;
        private string _activate = "", _deactivate = "";
        public GlobalHotkeys(IntPtr window) { _window = window; }

        public void Apply(string activate, string deactivate)
        {
            var a = Parse(activate);
            var d = Parse(deactivate);
            if (a != null && d != null && a.Key == d.Key && a.Modifiers == d.Modifiers)
                throw new ArgumentException("Choose different shortcuts for Activate and Deactivate.");
            Dispose();
            try
            {
                Register(ActivateId, a);
                Register(DeactivateId, d);
                _activate = activate; _deactivate = deactivate;
            }
            catch
            {
                Dispose();
                try { Register(ActivateId, Parse(_activate)); Register(DeactivateId, Parse(_deactivate)); }
                catch { Dispose(); }
                throw;
            }
        }

        // Startup / after recording: register whatever can be, each shortcut on its own, so one
        // combination taken by another app doesn't take the other one down with it.
        public string[] ApplyEach(string activate, string deactivate)
        {
            Dispose();
            _activate = _deactivate = "";
            var errors = new System.Collections.Generic.List<string>();
            try { Register(ActivateId, Parse(activate)); _activate = activate; }
            catch (Exception ex) { errors.Add("Activate shortcut: " + ex.Message); }
            if (!string.IsNullOrWhiteSpace(deactivate) && deactivate == _activate)
                errors.Add("Deactivate shortcut: same as Activate.");
            else
            {
                try { Register(DeactivateId, Parse(deactivate)); _deactivate = deactivate; }
                catch (Exception ex) { errors.Add("Deactivate shortcut: " + ex.Message); }
            }
            return errors.ToArray();
        }

        private static KeyGesture Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var gesture = (KeyGesture)new KeyGestureConverter().ConvertFromInvariantString(text);
            if ((gesture.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0)
                throw new ArgumentException("Use Ctrl, Alt or Win together with a key.");
            return gesture;
        }

        private void Register(int id, KeyGesture gesture)
        {
            if (gesture == null) return;
            uint modifiers = 0x4000; // MOD_NOREPEAT
            if (gesture.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= 1;
            if (gesture.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= 2;
            if (gesture.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= 4;
            if (gesture.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= 8;
            if (!RegisterHotKey(_window, id, modifiers, (uint)KeyInterop.VirtualKeyFromKey(gesture.Key)))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Shortcut unavailable: " + gesture.GetDisplayStringForCulture(System.Globalization.CultureInfo.CurrentCulture));
        }

        public void Dispose()
        {
            UnregisterHotKey(_window, ActivateId);
            UnregisterHotKey(_window, DeactivateId);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    }
}
