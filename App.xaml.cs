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

            // Prevent a second instance (manual start + autostart must not duplicate). A second manual
            // start brings the running one's window up instead of silently doing nothing; after a
            // self-update the new exe waits for the old one to let go first.
            bool createdNew;
            _mutex = new Mutex(true, "SteamTV_SingleInstance_8f2b1c", out createdNew);
            if (!createdNew && e.Args.Contains(Updater.WaitArg))
            {
                try { createdNew = _mutex.WaitOne(TimeSpan.FromSeconds(15)); }
                catch (AbandonedMutexException) { createdNew = true; }   // the old process exited holding it
            }
            if (!createdNew)
            {
                if (!autostart)
                    try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
                _mutex = null;   // not ours — must not be released on exit
                Shutdown();
                return;
            }
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

            Updater.CleanUpOldExe();
            AdbBundle.EnsureExtracted();

            ApplicationThemeManager.ApplySystemTheme();

            var window = new MainWindow(autostart);
            SystemThemeWatcher.Watch(window, Wpf.Ui.Controls.WindowBackdropType.None);
            MainWindow = window;
            window.Show();

            var showEvent = _showEvent;
            new Thread(() =>
            {
                while (true)
                {
                    try { showEvent.WaitOne(); } catch { return; }   // disposed on exit
                    Dispatcher.BeginInvoke(new Action(window.ShowFromTray));
                }
            }) { IsBackground = true, Name = "SteamTV-ShowRequests" }.Start();
        }

        private const string ShowEventName = "SteamTV_Show_8f2b1c";
        private EventWaitHandle _showEvent;

        protected override void OnExit(ExitEventArgs e)
        {
            try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { }
            _mutex?.Dispose();
            _showEvent?.Dispose();
            base.OnExit(e);
        }
    }

}
