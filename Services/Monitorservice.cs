// Background loop — core logic: reacts to gamepad and Big Picture.
using System;
using System.Threading;

namespace SteamTV
{

    internal sealed class MonitorService
    {
        private readonly AppSettings _s;
        private Thread _thread;
        private CancellationTokenSource _cts;
        private volatile bool _running;

        private DisplayConfigSnapshot _preActivateSnapshot;
        private PhysicalDisplayId? _targetPhysicalId;
        private bool _displayEnabled;

        public event Action<string> Log;
        public event Action<bool> RunningChanged;

        public bool IsRunning => _running;

        public MonitorService(AppSettings s) { _s = s; }

        private void L(string msg)
        {
            Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg);
            Log?.Invoke(msg);
        }

        public void Start()
        {
            if (_running) return;
            _cts = new CancellationTokenSource();
            _running = true;
            _displayEnabled = false;
            _preActivateSnapshot = null;
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

        private bool Sleep(CancellationToken ct, int ms) => ct.WaitHandle.WaitOne(ms);

        private void RunLoop(CancellationToken ct)
        {
            var adb = new Adb(_s.AdbPath, _s.TvIp);

            bool prevController = false;
            bool prevBp = false;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    bool curController = SteamHelper.IsWatchedControllerConnected(_s.WatchedGamepads);
                    bool curBp = SteamHelper.IsBigPictureRunning();

                    // --- ACTIVATION: watched gamepad just connected, Big Picture not yet running ---
                    if (curController && !prevController && !curBp)
                    {
                        L("Gamepad connected -> activating TV.");

                        if (_s.EnableWakeTV)
                            L(adb.WakeTv());

                        string err;
                        if (_s.DisableOthers)
                        {
                            _preActivateSnapshot = DisplayManager.QueryAll(out err);
                            if (_preActivateSnapshot == null) L("Failed to save monitor layout: " + err);

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
                        else
                        {
                            if (!DisplayManager.EnableDisplay(_s.TargetDisplay, out err))
                                L("Enable display: " + err);
                        }

                        _displayEnabled = true;

                        if (_s.EnableSourceSwitch)
                        {
                            adb.SwitchSourceToPc(_s.HdmiSourcePackage);

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
                                    SteamHelper.StartBigPicture(_s.SteamPath);
                                }
                            }
                            else
                            {
                                L("HDMI source did not activate within 10s.");
                            }
                        }
                        else if (_s.EnableBigPicture)
                        {
                            L("Starting Big Picture (source switch disabled).");
                            SteamHelper.StartBigPicture(_s.SteamPath);
                        }
                    }

                    // --- DEACTIVATION: Big Picture was running, now closed ---
                    if (_displayEnabled && prevBp && !curBp)
                    {
                        L("Big Picture closed -> deactivating TV.");

                        string err;
                        if (_s.DisableOthers && _preActivateSnapshot != null)
                        {
                            if (!DisplayManager.RestoreAll(_preActivateSnapshot, out err))
                                L("Restore monitors: " + err);
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

                        if (_s.EnableSourceSwitch)
                            adb.RestoreSourceBeforeShutdown(_s.TvHomeComponent);

                        adb.Disconnect();
                        SteamHelper.MinimizeSteamWindow();
                        _displayEnabled = false;
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
