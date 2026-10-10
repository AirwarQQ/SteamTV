// Background loop — core logic: reacts to trigger devices connecting and to Big Picture.
using System;
using System.Collections.Generic;
using System.Linq;
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

        // Set from the UI thread on WM_DEVICECHANGE so the loop reacts immediately instead of on its next 2 s tick.
        private readonly AutoResetEvent _devicesChanged = new AutoResetEvent(false);

        // Bluetooth controllers drop for a moment now and then (sleep, interference); don't tear the
        // whole TV session down over a blip.
        private static readonly TimeSpan RestoreGrace = TimeSpan.FromSeconds(10);

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

        public void NotifyDevicesChanged() => _devicesChanged.Set();

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
            if (!_s.WatchedDevices.Any(d => d.Enabled))
                L("WARNING: no enabled trigger devices — add one (and tick it) on the Devices tab.");
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
                    Activate(new Adb(_s.AdbPath, _s.TvIp), CancellationToken.None, _s.EnableBigPicture);
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

        // Full activation sequence: wake TV, enable/arrange displays, switch HDMI source, and (unless
        // launchBigPicture is false — a "TV only" trigger, or the global toggle off) launch Big Picture.
        // Used both by the automatic device-connect trigger and ActivateNow().
        private void Activate(Adb adb, CancellationToken ct, bool launchBigPicture)
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
                    if (launchBigPicture)
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
            else if (launchBigPicture)
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

            // Empty at start, so an enabled device that's already plugged in when monitoring starts
            // triggers right away — same as the old single-gamepad behaviour.
            var prevConnected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Big Picture may already be running if the monitor was stopped and restarted (or the app
            // restarted) mid-session — start in sync with reality so a later BP close still runs cleanup
            // instead of being skipped because _displayEnabled was reset to false by Start().
            bool prevBp = SteamHelper.IsBigPictureRunning();
            _displayEnabled = prevBp;
            if (prevBp)
                L("Big Picture already running — resuming as active.");

            // "Restore on disconnect" devices seen connected during the current active session, and
            // when the last of them went away (null while at least one is still around).
            var armed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            DateTime? goneSince = null;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var watched = _s.WatchedDevices;   // copy-on-write list: read the reference once per tick
                    var curConnected = new HashSet<string>(
                        DeviceScanner.Scan().Where(d => d.IsConnected).Select(d => d.Key),
                        StringComparer.OrdinalIgnoreCase);
                    bool curBp = SteamHelper.IsBigPictureRunning();

                    // Per-device edge, not "any watched device connected": otherwise an always-plugged
                    // keyboard on the list would keep that flag true forever and nothing could trigger.
                    var trigger = watched.FirstOrDefault(d =>
                        d.Enabled && curConnected.Contains(d.Key) && !prevConnected.Contains(d.Key));

                    // --- ACTIVATION: enabled trigger device just connected, TV not already active ---
                    // _displayEnabled matters now that "TV only" triggers don't start Big Picture: a second
                    // device connecting mid-session must not re-run Activate, which would re-snapshot the
                    // already-modified monitor layout and lose the real one to restore.
                    if (trigger != null && !curBp && !_displayEnabled)
                    {
                        bool tvOnly = trigger.Action == TriggerAction.TvOnly;
                        L(trigger.DisplayName + " connected -> activating TV" + (tvOnly ? " (TV only)." : "."));
                        lock (_actionLock)
                        {
                            Activate(adb, ct, launchBigPicture: !tvOnly && _s.EnableBigPicture);
                        }
                    }

                    // --- RESTORE ON DISCONNECT: every flagged device from this session gone for a while ---
                    if (_displayEnabled)
                    {
                        foreach (var d in watched)
                            if (d.Enabled && d.RestoreOnDisconnect && curConnected.Contains(d.Key))
                                armed.Add(d.Key);

                        if (armed.Count > 0 && !armed.Any(curConnected.Contains))
                        {
                            if (goneSince == null)
                            {
                                goneSince = DateTime.UtcNow;
                                L(NamesOf(watched, armed) + " disconnected — restoring in " + RestoreGrace.TotalSeconds + " s unless it comes back.");
                            }
                            else if (DateTime.UtcNow - goneSince.Value >= RestoreGrace)
                            {
                                L(NamesOf(watched, armed) + " still gone -> deactivating TV.");
                                lock (_actionLock)
                                {
                                    // Close Big Picture first — otherwise once the TV display is switched off it
                                    // reopens full-screen on the main monitor.
                                    if (curBp)
                                    {
                                        string how;
                                        if (SteamHelper.CloseBigPicture(ct, out how))
                                            L("Big Picture closed (" + how + ").");
                                        else
                                        {
                                            L("Big Picture " + how + ".");
                                            N("SteamTV", "Couldn't close Big Picture after the device disconnected.");
                                        }
                                    }
                                    Deactivate(adb, ct);
                                }
                                SteamHelper.MinimizeSteamWindow();
                                curBp = SteamHelper.IsBigPictureRunning();
                                armed.Clear();
                                goneSince = null;
                            }
                        }
                        else if (goneSince != null)
                        {
                            L("Device back — restore cancelled.");
                            goneSince = null;
                        }
                    }
                    else
                    {
                        armed.Clear();
                        goneSince = null;
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

                    prevConnected = curConnected;
                    prevBp = curBp;
                }
                catch (Exception ex)
                {
                    L("Loop error: " + ex.Message);
                }

                // Poll every 2 s, or right away when Windows reports a device change.
                int woke = WaitHandle.WaitAny(new[] { ct.WaitHandle, _devicesChanged }, 2000);
                if (woke == 0) return;
                // A plug-in is a burst of devnode changes (and Bluetooth flips IsConnected a moment
                // later) — give the rest of it time to land so one scan sees the finished state.
                if (woke == 1 && Sleep(ct, 400)) return;
            }
        }

        private static string NamesOf(List<WatchedDevice> watched, HashSet<string> keys) =>
            string.Join(", ", watched.Where(d => keys.Contains(d.Key)).Select(d => d.DisplayName));
    }

}
