// SteamTV — entry point.
// See README.md for project description, build options, and settings location.
using System;
using System.Linq;
using System.Threading;
using System.Windows;
using Wpf.Ui.Appearance;

namespace SteamTV
{

    public partial class App : Application
    {
        private Mutex _mutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            bool autostart = e.Args.Any(a =>
                a.Equals("-autostart", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("/autostart", StringComparison.OrdinalIgnoreCase));

            // Prevent a second instance (manual start + autostart must not duplicate)
            bool createdNew;
            _mutex = new Mutex(true, "SteamTV_SingleInstance_8f2b1c", out createdNew);
            if (!createdNew)
            {
                Shutdown();
                return;
            }

            ApplicationThemeManager.ApplySystemTheme();

            var window = new MainWindow(autostart);
            SystemThemeWatcher.Watch(window, Wpf.Ui.Controls.WindowBackdropType.None);
            MainWindow = window;
            window.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
            base.OnExit(e);
        }
    }

}
