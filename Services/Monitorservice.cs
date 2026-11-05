// Background loop — core logic: reacts to trigger devices connecting and to Big Picture.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NekoCouch
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
        // whole TV session down over a blip. How long counts as a blip is a setting.
        private TimeSpan RestoreGrace => TimeSpan.FromSeconds(_s.RestoreGraceSec);

        private DisplayConfigSnapshot _preActivateSnapshot;
        private PhysicalDisplayId? _targetPhysicalId;
        private volatile bool _displayEnabled;

        // What this session changed, so deactivation puts exactly that back.
        private bool _tvWasAsleep;          // NekoCouch woke it → may put it back to sleep
        private string _tvPreviousApp;      // package/.Activity the TV showed before the switch
        private string _audioPreviousId;    // default playback device before switching to the TV
        private bool? _hdrPrevious;         // TV display's HDR state before NekoCouch turned it on

        private Adb NewAdb() => new Adb(_s.AdbPath, _s.TvIp);   // TvIp can change (found again after DHCP)

        private void MinimizeSteam() { if (_s.MinimizeSteamAfter) SteamHelper.MinimizeSteamWindow(); }

        // What an activation or deactivation is doing right now, for the Home page: a Loc key
        // ("Phase.Wake", …) plus its argument, or null when idle.
        public string PhaseKey { get; private set; }
        public object PhaseArg { get; private set; }
        public event Action PhaseChanged;

        private void Phase(string key, object arg = null)
        {
            PhaseKey = key;
            PhaseArg = arg;
            PhaseChanged?.Invoke();
        }

        public event Action<string> Log;
        public event Action<bool> RunningChanged;
        // Raised (on whatever thread changed it) whenever IsDisplayActive flips — automatic
        // activation, Big Picture closing, restore-on-disconnect, rollback or the manual buttons.
        public event Action<bool> DisplayActiveChanged;
        // Fired for failures worth interrupting the user for (tray balloon), separate from the
        // full step-by-step Log so routine activity doesn't spam a notification.
        public event Action<string, string> Notify;
        // The TV turned up under a different IP (after a DHCP change); settings are already saved.
        public event Action<string> TvAddressChanged;

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

        private void SetDisplayEnabled(bool value)
        {
            if (_displayEnabled == value) return;
            _displayEnabled = value;
            DisplayActiveChanged?.Invoke(value);
        }

        public void Start()
        {
            if (_running) return;
            _cts = new CancellationTokenSource();
            _running = true;
            _thread = new Thread(() => RunLoop(_cts.Token)) { IsBackground = true, Name = "NekoCouch-Monitor" };
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
                    Activate(NewAdb(), CancellationToken.None, _s.EnableBigPicture);
                }
            });
        }

        // Manual override — restores displays/TV source regardless of whether the app thinks
        // it's currently "active", so it also works as a recovery button if state ever desyncs.
        // Closes Big Picture first, same as restore-on-disconnect — also when it was started by hand
        // and the TV was never "active".
        public Task DeactivateNow()
        {
            return Task.Run(() =>
            {
                lock (_actionLock)
                {
                    L("Manual deactivate requested.");
                    CloseBigPictureBeforeDeactivate(CancellationToken.None);
                    Deactivate(NewAdb(), CancellationToken.None);
                    MinimizeSteam();
                }
            });
        }

        // Big Picture has to go before the TV display is switched off — otherwise it reopens full-screen
        // on the main monitor. A failure is reported but doesn't stop the deactivation.
        private void CloseBigPictureBeforeDeactivate(CancellationToken ct)
        {
            if (!SteamHelper.IsBigPictureRunning()) return;
            string how;
            if (SteamHelper.CloseBigPicture(ct, out how))
                L("Big Picture closed (" + how + ").");
            else
            {
                L("Big Picture " + how + " — deactivating anyway.");
                N("NekoCouch", Loc.T("Note.BpNotClosed"));
            }
        }

        private bool Sleep(CancellationToken ct, int ms) => ct.WaitHandle.WaitOne(ms);

        // Restores the pre-activation monitor layout (or just disables the TV display if
        // "disable others" was off). Shared by normal deactivation and the source-switch-failed
        // rollback, since both need to undo exactly the same display change.
        private void RestoreDisplayState()
        {
            string err;
            // HDR first — it's a per-display setting Windows remembers, and it can only be changed
            // while the TV display is still on.
            if (_hdrPrevious != null)
            {
                if (DisplayManager.SetHdr(_s.TargetDisplay, _hdrPrevious.Value, out err)) L("TV HDR set back to " + (_hdrPrevious.Value ? "on." : "off."));
                else L("Restore HDR: " + err);
                _hdrPrevious = null;
            }
            if (_audioPreviousId != null)
            {
                if (AudioSwitcher.SetDefault(_audioPreviousId, out err)) L("Sound back on the previous device.");
                else L("Restore sound device: " + err);
                _audioPreviousId = null;
            }

            if (_s.DisableOthers && _preActivateSnapshot != null)
            {
                if (!DisplayManager.RestoreAll(_preActivateSnapshot, out err))
                {
                    L("Restore monitors FAILED: " + err);
                    N("NekoCouch", Loc.T("Note.RestoreFailed", err));
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
            Phase("Phase.Start");
            try { ActivateCore(adb, ct, launchBigPicture); }
            finally { Phase(null); }
        }

        private void ActivateCore(Adb adb, CancellationToken ct, bool launchBigPicture)
        {
            L("Activating TV.");
            _tvWasAsleep = false;
            _tvPreviousApp = null;

            // Without ADB nothing below can reach the TV: find out now, not after the monitors have
            // been rearranged and every adb call has waited out its timeout.
            if (_s.EnableWakeTV || _s.EnableSourceSwitch)
            {
                if (!EnsureTvReachable(adb, ct))
                {
                    L("TV not reachable over ADB — activation cancelled.");
                    N("NekoCouch", Loc.T("Note.Unreachable", _s.TvIp));
                    return;
                }
                adb = NewAdb();   // EnsureTvReachable may have found the TV under a new address
            }

            if (_s.EnableWakeTV)
            {
                Phase("Phase.Wake");
                L(adb.WakeTv(out _tvWasAsleep));
                if (_tvWasAsleep && Sleep(ct, 1500)) return;   // let the TV finish waking before asking it things
            }
            if (_s.EnableSourceSwitch && _s.RestoreTvApp && !_tvWasAsleep)
            {
                _tvPreviousApp = adb.GetForegroundComponent();
                // Already on an HDMI input: nothing worth going back to — the launcher is better.
                if (_tvPreviousApp != null && (_tvPreviousApp.StartsWith("com.xiaomi.mitv.tvplayer/", StringComparison.Ordinal) ||
                                               _tvPreviousApp.StartsWith("com.liskovsoft.tvexternalsource", StringComparison.Ordinal)))
                    _tvPreviousApp = null;
                if (_tvPreviousApp != null) L("TV was showing " + _tvPreviousApp + ".");
            }

            Phase("Phase.Display");
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

            SetDisplayEnabled(true);

            if (Sleep(ct, 500)) return;
            string rrErr;
            if (!DisplayManager.SetHighestRefreshRate(_s.TargetDisplay, out rrErr, L))
                L("Set refresh rate: " + rrErr);

            if (_s.EnableSourceSwitch)
            {
                Phase("Phase.Hdmi", _s.HdmiPort);
                string pattern = null, method = null, switchErr = null;
                try { pattern = adb.SwitchToHdmi(_s.HdmiPort, out method, ct); }
                catch (InvalidOperationException ex) { switchErr = ex.Message; }
                if (ct.IsCancellationRequested) return;
                if (!string.IsNullOrWhiteSpace(_s.HdmiActivityPattern)) pattern = _s.HdmiActivityPattern;

                bool active = false;
                if (switchErr == null)
                {
                    L("Switching to HDMI " + _s.HdmiPort + " via " + method + ".");
                    if (pattern == null)
                    {
                        // The legacy app opens a vendor screen we can't name — give it time instead.
                        if (Sleep(ct, 2000)) return;
                        active = true;
                    }
                    else
                    {
                        double elapsed = 0;
                        while (!(active = adb.IsHdmiSourceActive(pattern)) && elapsed < 10.0)
                        {
                            if (Sleep(ct, 200)) return;
                            elapsed += 0.2;
                        }
                    }
                }

                if (active)
                {
                    if (Sleep(ct, 1000)) return;
                    L("HDMI source active.");
                    ApplyHdrAndAudio(ct);
                    if (launchBigPicture)
                    {
                        L("Starting Big Picture.");
                        Phase("Phase.BigPicture");
                        string bpErr;
                        if (!SteamHelper.StartBigPicture(_s.SteamPath, out bpErr))
                        {
                            L("Start Big Picture FAILED: " + bpErr);
                            N("NekoCouch", Loc.T("Note.BpFailed", bpErr));
                        }
                    }
                }
                else
                {
                    L((switchErr ?? "HDMI source did not activate within 10s.") + " Rolling back.");
                    N("NekoCouch", switchErr != null ? Loc.T("Note.SwitchFailed", switchErr) : Loc.T("Note.SwitchTimeout"));
                    RestoreDisplayState();
                    L("TV: " + adb.ReturnTv(_tvPreviousApp, _s.TvHomeComponent, _s.SleepTvAfter && _tvWasAsleep, ct) + ".");
                    SetDisplayEnabled(false);
                }
            }
            else
            {
                ApplyHdrAndAudio(ct);
                if (!launchBigPicture) return;
                L("Starting Big Picture (source switch disabled).");
                string bpErr;
                if (!SteamHelper.StartBigPicture(_s.SteamPath, out bpErr))
                {
                    L("Start Big Picture FAILED: " + bpErr);
                    N("NekoCouch", Loc.T("Note.BpFailed", bpErr));
                }
            }
        }

        // ADB answers → true. If not: wake the TV over the network (needs its MAC, learned the first
        // time it was reachable) and wait for it, then look for it under a new address in case DHCP
        // moved it.
        private bool EnsureTvReachable(Adb adb, CancellationToken ct)
        {
            if (adb.IsAdbReachable(3000)) { LearnTvMac(); return true; }
            if (string.IsNullOrEmpty(_s.TvMac)) return false;

            L("TV not answering — sending Wake-on-LAN to " + _s.TvMac + ".");
            TvDiscovery.SendWakeOnLan(_s.TvMac);
            for (int i = 0; i < 8; i++)
            {
                if (Sleep(ct, 2500)) return false;
                if (adb.IsAdbReachable(2500)) { L("TV woke up."); return true; }
            }

            string ip = TvDiscovery.FindByMacAsync(_s.TvMac).GetAwaiter().GetResult();
            if (ip == null || ip == _s.TvIp) return false;
            L("TV found at a new address: " + ip + " (was " + _s.TvIp + ").");
            _s.TvIp = ip;
            _s.Save();
            TvAddressChanged?.Invoke(ip);
            return NewAdb().IsAdbReachable(3000);
        }

        private void LearnTvMac()
        {
            string mac = TvDiscovery.MacOf(_s.TvIp);
            if (mac == null || mac == _s.TvMac) return;
            _s.TvMac = mac;
            _s.Save();
        }

        // TV-side extras once the picture is on the TV: HDR on, sound to the TV. Both are undone in
        // RestoreDisplayState().
        private void ApplyHdrAndAudio(CancellationToken ct)
        {
            string err;
            if (_s.EnableHdr)
            {
                bool? hdr = DisplayManager.GetHdr(_s.TargetDisplay);
                if (hdr == null) L("HDR: the TV display doesn't report HDR support.");
                else if (hdr == true) L("HDR already on.");
                else
                {
                    if (DisplayManager.SetHdr(_s.TargetDisplay, true, out err)) { _hdrPrevious = false; L("HDR on."); }
                    else L("HDR: " + err);
                }
            }

            if (_s.SwitchAudio)
            {
                Phase("Phase.Sound");
                string hint = string.IsNullOrWhiteSpace(_s.TvAudioDevice) ? DisplayManager.GetFriendlyName(_s.TargetDisplay) : _s.TvAudioDevice;
                var tv = AudioSwitcher.WaitForDevice(hint, 8000, ct);
                string current = AudioSwitcher.GetDefaultId();
                if (tv == null) L("Sound: no playback device matching \"" + hint + "\" — left as is.");
                else if (tv.Id == current) L("Sound already on " + tv.Name + ".");
                else if (AudioSwitcher.SetDefault(tv.Id, out err)) { _audioPreviousId = current; L("Sound → " + tv.Name + "."); }
                else L("Sound: " + err);
            }
        }

        // Full deactivation sequence: restore displays, send TV back to its home screen,
        // disconnect adb. Used both by the automatic Big-Picture-closed trigger and
        // DeactivateNow() (which also minimizes Steam itself — the automatic path does that
        // separately since it wants it right after this, not as part of the shared sequence).
        private void Deactivate(Adb adb, CancellationToken ct)
        {
            Phase("Phase.Back");
            try { DeactivateCore(adb, ct); }
            finally { Phase(null); }
        }

        private void DeactivateCore(Adb adb, CancellationToken ct)
        {
            L("Deactivating TV.");
            RestoreDisplayState();

            if (_s.EnableSourceSwitch)
                L("TV: " + adb.ReturnTv(_tvPreviousApp, _s.TvHomeComponent, _s.SleepTvAfter && _tvWasAsleep, ct) + ".");
            else if (_s.SleepTvAfter && _tvWasAsleep)
                adb.SleepTv();
            _tvWasAsleep = false;
            _tvPreviousApp = null;

            adb.Disconnect();
            SetDisplayEnabled(false);
        }

        private void RunLoop(CancellationToken ct)
        {
            // A fresh Adb per action: the TV address may change while monitoring (found again after DHCP).

            // Empty at start, so an enabled device that's already plugged in when monitoring starts
            // triggers right away — same as the old single-gamepad behaviour.
            var prevConnected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Big Picture may already be running if the monitor was stopped and restarted (or the app
            // restarted) mid-session — start in sync with reality so a later BP close still runs cleanup
            // instead of being skipped because _displayEnabled was reset to false by Start().
            bool prevBp = SteamHelper.IsBigPictureRunning();
            SetDisplayEnabled(prevBp);
            if (prevBp)
                L("Big Picture already running — resuming as active.");

            // "Restore on disconnect" devices seen connected during the current active session, and
            // when the last of them went away (null while at least one is still around).
            var armed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            DateTime? goneSince = null;

            // Enumerating every devnode is the expensive part of a tick. Windows announces plug-ins
            // (WM_DEVICECHANGE wakes this loop), so scan right after one — and for a few seconds
            // more, since Bluetooth flips "connected" a moment later — and otherwise only every 15 s
            // as a safety net. The cheap Big Picture check still runs every 2 s.
            var curConnected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            DateTime lastScan = DateTime.MinValue, scanBurstUntil = DateTime.UtcNow.AddSeconds(6);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var watched = _s.WatchedDevices;   // copy-on-write list: read the reference once per tick
                    if (DateTime.UtcNow < scanBurstUntil || DateTime.UtcNow - lastScan > TimeSpan.FromSeconds(15)
                        || (_displayEnabled && goneSince != null))   // a restore countdown needs fresh state
                    {
                        curConnected = new HashSet<string>(
                            DeviceScanner.Scan().Where(d => d.IsConnected).Select(d => d.Key),
                            StringComparer.OrdinalIgnoreCase);
                        lastScan = DateTime.UtcNow;
                    }
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
                        lock (_actionLock)
                        {
                            // Re-checked under the lock: a manual Activate (button / Home Assistant) may
                            // have just run, and a second pass would snapshot the already-changed layout.
                            if (!_displayEnabled)
                            {
                                L(trigger.DisplayName + " connected -> activating TV" + (tvOnly ? " (TV only)." : "."));
                                Activate(NewAdb(), ct, launchBigPicture: !tvOnly && _s.EnableBigPicture);
                            }
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
                                lock (_actionLock)
                                {
                                    // Re-checked under the lock: a manual Deactivate may have finished meanwhile.
                                    if (_displayEnabled)
                                    {
                                        L(NamesOf(watched, armed) + " still gone -> deactivating TV.");
                                        CloseBigPictureBeforeDeactivate(ct);
                                        Deactivate(NewAdb(), ct);
                                        MinimizeSteam();
                                    }
                                }
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
                    // The check above runs outside the lock, so it also fires right after DeactivateNow()
                    // closed Big Picture itself while still restoring — re-check under the lock, or the
                    // restore would run twice (and the second pass, with no snapshot left, switches the
                    // TV display off even if it was on before activation).
                    if (_displayEnabled && prevBp && !curBp)
                    {
                        lock (_actionLock)
                        {
                            if (_displayEnabled)
                            {
                                L("Big Picture closed -> deactivating TV.");
                                Deactivate(NewAdb(), ct);
                                MinimizeSteam();
                            }
                        }
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
                if (woke == 1)
                {
                    scanBurstUntil = DateTime.UtcNow.AddSeconds(6);
                    if (Sleep(ct, 400)) return;
                }
            }
        }

        private static string NamesOf(List<WatchedDevice> watched, HashSet<string> keys) =>
            string.Join(", ", watched.Where(d => keys.Contains(d.Key)).Select(d => d.DisplayName));
    }

}
