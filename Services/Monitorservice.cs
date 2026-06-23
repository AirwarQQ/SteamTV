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

        // true -> cancelled
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
                    bool curController = SteamHelper.IsControllerConnected();
                    bool curBp = SteamHelper.IsBigPictureRunning();

                    // --- ACTIVATION: gamepad just connected, Big Picture not yet running ---
                    if (curController && !prevController && !curBp)
                    {
                        L("Gamepad connected -> activating TV.");
                        L(adb.WakeTv());

                        string err;
                        if (_s.DisableOthers)
                        {
                            _preActivateSnapshot = DisplayManager.QueryAll(out err);
                            if (_preActivateSnapshot == null) L("Failed to save monitor layout: " + err);

                            // Resolve target display to its stable physical ID NOW (before any config changes)
                            _targetPhysicalId = DisplayManager.ResolvePhysicalId(_s.TargetDisplay, out err);
                            if (_targetPhysicalId == null)
                            {
                                L("Cannot resolve physical ID for display #" + _s.TargetDisplay + ": " + err);
                            }
                            else
                            {
                                L("Target physical ID resolved: " + _targetPhysicalId.Value.AdapterId.LowPart + "/" + _targetPhysicalId.Value.TargetId);
                            }

                            if (!DisplayManager.EnableDisplay(_s.TargetDisplay, out err))
                                L("Enable display: " + err);
                            else
                            {
                                L("Display #" + _s.TargetDisplay + " enabled successfully.");
                                // Wait for Windows to apply the display change before querying again
                                L("Waiting 1.5s for Windows to apply display change...");
                                if (Sleep(ct, 1500)) return;
                            }

                            // Use physical ID for disabling — immune to DISPLAY number reassignment
                            if (_targetPhysicalId != null)
                            {
                                if (!DisplayManager.DisableAllExceptPhysical(_targetPhysicalId.Value, out err, L))
                                    L("Disable other monitors FAILED: " + err);
                                else
                                    L("Only target display left active (by physical ID).");
                            }
                            else
                            {
                                // Fallback to legacy method if resolution failed
                                if (!DisplayManager.DisableAllExcept(_s.TargetDisplay, out err, L))
                                    L("Disable other monitors FAILED: " + err);
                                else
                                    L("Only display #" + _s.TargetDisplay + " left active.");
                            }
                        }
                        else
                        {
                            if (!DisplayManager.EnableDisplay(_s.TargetDisplay, out err))
                                L("Enable display: " + err);
                        }
                        _displayEnabled = true;

                        adb.SwitchSourceToPc(_s.HdmiSourcePackage);

                        // wait for HDMI source to appear (up to 10 seconds)
                        double elapsed = 0;
                        while (!adb.IsHdmiSourceActive(_s.HdmiActivityPattern) && elapsed < 10.0)
                        {
                            if (Sleep(ct, 200)) return;
                            elapsed += 0.2;
                        }

                        if (adb.IsHdmiSourceActive(_s.HdmiActivityPattern))
                        {
                            if (Sleep(ct, 1000)) return;
                            L("HDMI source active -> starting Big Picture.");
                            SteamHelper.StartBigPicture(_s.SteamPath);
                        }
                        else
                        {
                            L("HDMI source did not activate within 10s.");
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