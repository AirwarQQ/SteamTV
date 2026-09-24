// Background loop — core logic: reacts to gamepad and Big Picture.
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SteamTV
{

    internal sealed class MonitorService
    {
        private readonly AppSettings _s;
        private Thread _thread;
        private CancellationTokenSource _cts;
        private volatile bool _running;

        // Guards Activate()/Deactivate() against overlapping with each other, whether triggered
        // by the automatic loop or a manual Activate/Deactivate button.
        private readonly object _actionLock = new object();

        private DisplayConfigSnapshot _preActivateSnapshot;
        private PhysicalDisplayId? _targetPhysicalId;
        private bool _displayEnabled;

        public event Action<string> Log;
        public event Action<bool> RunningChanged;
        // Fired for failures worth interrupting the user for (tray balloon), separate from the
        // full step-by-step Log so routine activity doesn't spam a notification.
        public event Action<string, string> Notify;

        public bool IsRunning => _running;
        public bool IsDisplayActive => _displayEnabled;

        public MonitorService(AppSettings s) { _s = s; }

        private void L(string msg)
        {
            Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg);
            Log?.Invoke(msg);
        }

        private void N(string title, string msg) => Notify?.Invoke(title, msg);

        public void Start()
        {
            if (_running) return;
            _cts = new CancellationTokenSource();
            _running = true;
            _thread = new Thread(() => RunLoop(_cts.Token)) { IsBackground = true, Name = "SteamTV-Monitor" };
            _thread.Start();
            RunningChanged?.Invoke(true);
            L("Monitor started. IP=" + _s.TvIp + ", display #" + _s.TargetDisplay +
              (_s.DisableOthers ? ", other monitors will be disabled." : "."));
            if (_s.WatchedGamepads.Count == 0)
                L("WARNING: no gamepads configured — add at least one in the Gamepads tab.");
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            try { _cts.Cancel(); } catch { }
            try { _thread?.Join(3000); } catch { }
            RunningChanged?.Invoke(false);
            L("Monitor stopped.");
        }

        // Manual override — runs the same activation sequence as an automatic gamepad-connect
        // trigger, on demand (e.g. the TV was never/no longer correctly detected as active).
        // No-op if already active, since re-running it while DisableOthers is on would overwrite
        // _preActivateSnapshot with the already-modified layout, corrupting the real restore point.
        public void ActivateNow()
        {
            Task.Run(() =>
            {
                lock (_actionLock)
                {
                    if (_displayEnabled)
                    {
                        L("Manual activate: already active — ignoring.");
                        return;
                    }
                    L("Manual activate requested.");
                    Activate(new Adb(_s.AdbPath, _s.TvIp), CancellationToken.None);
                }
            });
        }

        // Manual override — restores displays/TV source regardless of whether the app thinks
        // it's currently "active", so it also works as a recovery button if state ever desyncs.
        public void DeactivateNow()
        {
            Task.Run(() =>
            {
                lock (_actionLock)
                {
                    L("Manual deactivate requested.");
                    Deactivate(new Adb(_s.AdbPath, _s.TvIp), CancellationToken.None);
                    SteamHelper.MinimizeSteamWindow();
                }
            });
        }

        private bool Sleep(CancellationToken ct, int ms) => ct.WaitHandle.WaitOne(ms);

        // Restores the pre-activation monitor layout (or just disables the TV display if
        // "disable others" was off). Shared by normal deactivation and the source-switch-failed
        // rollback, since both need to undo exactly the same display change.
        private void RestoreDisplayState()
        {
            string err;
            if (_s.DisableOthers && _preActivateSnapshot != null)
            {
                if (!DisplayManager.RestoreAll(_preActivateSnapshot, out err))
                {
                    L("Restore monitors FAILED: " + err);
                    N("SteamTV", "Failed to restore monitor layout: " + err);
                }
                else if (!string.IsNullOrEmpty(err))
                    L("Restore monitors (fallback): " + err);
                else
                    L("Monitor layout restored.");
                _preActivateSnapshot = null;
                _targetPhysicalId = null;
            }
            else
            {
                if (!DisplayManager.DisableDisplay(_s.TargetDisplay, out err))
                    L("Disable display: " + err);
            }
        }

        // Full activation sequence: wake TV, enable/arrange displays, switch HDMI source, launch
        // Big Picture. Used both by the automatic gamepad-connect trigger and ActivateNow().
        private void Activate(Adb adb, CancellationToken ct)
        {
            L("Activating TV.");

            if (_s.EnableWakeTV)
                L(adb.WakeTv());

            string err;
            if (_s.DisableOthers)
            {
                _preActivateSnapshot = DisplayManager.QueryAll(out err);
                if (_preActivateSnapshot == null)
                {
                    L("Failed to save monitor layout: " + err + " — skipping monitor disable to avoid unrecoverable state.");
                    if (!DisplayManager.EnableDisplay(_s.TargetDisplay, out err))
                        L("Enable display: " + err);
                }
                else
                {
                    _targetPhysicalId = DisplayManager.ResolvePhysicalId(_s.TargetDisplay, out err);
                    if (_targetPhysicalId == null)
                        L("Cannot resolve physical ID for display #" + _s.TargetDisplay + ": " + err);

                    if (!DisplayManager.EnableDisplay(_s.TargetDisplay, out err))
                        L("Enable display: " + err);
                    else
                    {
                        L("Display #" + _s.TargetDisplay + " enabled.");
                        L("Waiting 1.5s for Windows to apply display change...");
                        if (Sleep(ct, 1500)) return;
                    }

                    if (_targetPhysicalId != null)
                    {
                        if (!DisplayManager.DisableAllExceptPhysical(_targetPhysicalId.Value, out err, L))
                            L("Disable other monitors FAILED: " + err);
                        else
                            L("Only target display active.");
                    }
                    else
                    {
                        if (!DisplayManager.DisableAllExcept(_s.TargetDisplay, out err, L))
                            L("Disable other monitors FAILED: " + err);
                        else
                            L("Only display #" + _s.TargetDisplay + " active.");
                    }
                }
            }
            else
            {
                if (!DisplayManager.EnableDisplay(_s.TargetDisplay, out err))
                    L("Enable display: " + err);
            }

            _displayEnabled = true;

            if (Sleep(ct, 500)) return;
            string rrErr;
            if (!DisplayManager.SetHighestRefreshRate(_s.TargetDisplay, out rrErr, L))
                L("Set refresh rate: " + rrErr);

            if (_s.EnableSourceSwitch)
            {
                adb.SwitchSourceToPc(_s.HdmiSourcePackage, ct);

                double elapsed = 0;
                while (!adb.IsHdmiSourceActive(_s.HdmiActivityPattern) && elapsed < 10.0)
                {
                    if (Sleep(ct, 200)) return;
                    elapsed += 0.2;
                }

                if (adb.IsHdmiSourceActive(_s.HdmiActivityPattern))
                {
                    if (Sleep(ct, 1000)) return;
                    L("HDMI source active.");
                    if (_s.EnableBigPicture)
                    {
                        L("Starting Big Picture.");
                        string bpErr;
                        if (!SteamHelper.StartBigPicture(_s.SteamPath, out bpErr))
                        {
                            L("Start Big Picture FAILED: " + bpErr);
                            N("SteamTV", "Steam Big Picture failed to start: " + bpErr);
                        }
                    }
                }
                else
                {
                    L("HDMI source did not activate within 10s — rolling back.");
                    N("SteamTV", "TV didn't switch to the PC's HDMI input in time — rolled back.");
                    RestoreDisplayState();
                    adb.RestoreSourceBeforeShutdown(_s.TvHomeComponent, ct);
                    adb.Disconnect();
                    _displayEnabled = false;
                }
            }
            else if (_s.EnableBigPicture)
            {
                L("Starting Big Picture (source switch disabled).");
                string bpErr;
                if (!SteamHelper.StartBigPicture(_s.SteamPath, out bpErr))
                {
                    L("Start Big Picture FAILED: " + bpErr);
                    N("SteamTV", "Steam Big Picture failed to start: " + bpErr);
                }
            }
        }

        // Full deactivation sequence: restore displays, send TV back to its home screen,
        // disconnect adb. Used both by the automatic Big-Picture-closed trigger and
        // DeactivateNow() (which also minimizes Steam itself — the automatic path does that
        // separately since it wants it right after this, not as part of the shared sequence).
        private void Deactivate(Adb adb, CancellationToken ct)
        {
            L("Deactivating TV.");
            RestoreDisplayState();

            if (_s.EnableSourceSwitch)
                adb.RestoreSourceBeforeShutdown(_s.TvHomeComponent, ct);

            adb.Disconnect();
            _displayEnabled = false;
        }

        private void RunLoop(CancellationToken ct)
        {
            var adb = new Adb(_s.AdbPath, _s.TvIp);

            bool prevController = false;
            // Big Picture may already be running if the monitor was stopped and restarted (or the app
            // restarted) mid-session — start in sync with reality so a later BP close still runs cleanup
            // instead of being skipped because _displayEnabled was reset to false by Start().
            bool prevBp = SteamHelper.IsBigPictureRunning();
            _displayEnabled = prevBp;
            if (prevBp)
                L("Big Picture already running — resuming as active.");

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    bool curController = SteamHelper.IsWatchedControllerConnected(_s.WatchedGamepads, L);
                    bool curBp = SteamHelper.IsBigPictureRunning();

                    // --- ACTIVATION: watched gamepad just connected, Big Picture not yet running ---
                    if (curController && !prevController && !curBp)
                    {
                        L("Gamepad connected -> activating TV.");
                        lock (_actionLock)
                        {
                            Activate(adb, ct);
                        }
                    }

                    // --- DEACTIVATION: Big Picture was running, now closed ---
                    if (_displayEnabled && prevBp && !curBp)
                    {
                        L("Big Picture closed -> deactivating TV.");
                        lock (_actionLock)
                        {
                            Deactivate(adb, ct);
                        }
                        SteamHelper.MinimizeSteamWindow();
                    }

                    prevController = curController;
                    prevBp = curBp;
                }
                catch (Exception ex)
                {
                    L("Loop error: " + ex.Message);
                }

                if (Sleep(ct, 2000)) return;
            }
        }
    }

}
