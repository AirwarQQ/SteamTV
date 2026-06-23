# SteamTV

Windows utility that watches for a gamepad connection and automatically switches your TV to PC input, enables the TV display in Windows, and launches Steam Big Picture. When Big Picture is closed, it restores your monitor layout and switches the TV back to its home screen.

Originally a PowerShell script, rewritten in C# with a proper UI.

---

## How it works

1. **Gamepad connects** → wake TV via ADB, enable TV display in Windows, disable other monitors (optional), switch TV HDMI input to PC, launch Steam Big Picture.
2. **Big Picture closes** → restore monitor layout, switch TV back to home screen, minimize Steam.

---

## Requirements

| Requirement | Notes |
|---|---|
| **Windows 10 / 11** | — |
| **.NET Framework 4.8** | Pre-installed on Windows 10 1903+ and all Windows 11. [Download](https://dotnet.microsoft.com/download/dotnet-framework/net48) if missing. |
| **Android Debug Bridge (`adb.exe`)** | Part of [Android Platform Tools](https://developer.android.com/tools/releases/platform-tools). Only needed for TV wake / source switching. |
| **Steam** | Standard install at `C:\Program Files (x86)\Steam\steam.exe` or configure the path in settings. |
| **TV with ADB over Wi-Fi** | Enable *Developer options → USB debugging* (or *Wireless debugging*) on your TV, then allow the ADB connection from your PC. |
| **TV External Source app** | [com.liskovsoft.tvexternalsource](https://github.com/yuliskov/SmartTubeNext) — installed on the TV; used to switch HDMI inputs via `adb shell monkey`. |

---

## Setup

### 1. Enable ADB on your TV
- Go to **Settings → Device Preferences → About → Build number** — tap 7 times to unlock Developer Options.
- In **Developer Options**, enable **USB Debugging** (ADB).
- Find your TV's IP address under **Settings → Network**.

### 2. Pair adb with the TV (first time only)
```
adb connect <TV_IP>
```
Accept the connection prompt on the TV.

### 3. Build
```
dotnet build -c Release
```
Output: `bin\Release\net48\SteamTV.exe`

Or open in Visual Studio 2022+ and press **Build**.

### 4. Configure in the app
Open **SteamTV.exe** and fill in the **Monitor** tab:

| Field | Description |
|---|---|
| **TV IP** | IP address of your TV on the local network |
| **HDMI port** | Port number your PC is connected to (1–4); used by the TV External Source app |
| **Target display #** | Windows display number for the TV — click **Displays…** to find it |

Click **Start** to begin monitoring. The ADB indicator (top-right) shows reachability, refreshed every 15 s.

### 5. Add your gamepad
Go to the **Gamepads** tab → click **Refresh** → select your gamepad from the "Connected right now" list → click **↑ Add to Watched**.

The monitoring loop triggers when any watched gamepad connects.

### 6. Autostart (optional)
On the **Monitor** tab, check **Run at Windows startup (hidden in tray)**. The app will start minimised to tray and begin monitoring automatically.

To control this from the tray icon: right-click → **Autostart app** / **Auto-start monitoring on launch**.

---

## Settings

All settings are stored in the registry at `HKCU\Software\SteamTV`.

Advanced paths (`adb.exe`, `steam.exe`, Android component names, HDMI activity pattern) can be edited directly in the registry if the defaults don't match your setup.

**Default paths:**
- `adb.exe` — `C:\migrate\pc\steamTV\final3\platform-tools\adb.exe` *(change to your actual path)*
- `steam.exe` — `C:\Program Files (x86)\Steam\steam.exe`

---

## Feature toggles

All toggles are on the **Monitor** tab under **Features**:

| Toggle | Effect |
|---|---|
| Disable other monitors when TV is active | Keeps only the TV display active while Big Picture is running; restores the full layout on exit |
| Wake TV via ADB on gamepad connect | Sends a power keyevent if the TV screen is off |
| Switch HDMI source on TV | Launches the TV External Source app to switch to the PC's HDMI port |
| Launch Steam Big Picture | Starts Steam in Big Picture mode after the source switches |

---

## Project structure

```
SteamTV/
├─ Program.cs              — entry point (single-instance mutex, -autostart flag)
├─ Appsettings.cs          — registry-backed settings + GamepadEntry model
├─ MainForm.cs             — tab UI: Monitor, Gamepads, Test; tray icon
├─ app.manifest            — PerMonitorV2 DPI awareness (crisp on HiDPI displays)
├─ App.config              — WinForms DPI opt-in
├─ Interop/
│  └─ Native.cs            — P/Invoke: user32 CCD display API, dwmapi
├─ Services/
│  ├─ Displaymanager.cs    — enable/disable/restore monitors via SetDisplayConfig
│  ├─ Adb.cs               — adb.exe wrapper (wake, source switch, reachability check)
│  ├─ Steamhelper.cs       — gamepad detection (WMI/HID), Big Picture, Steam
│  └─ Monitorservice.cs    — background loop: reacts to gamepad + Big Picture state
└─ Displaytest.cs          — standalone CLI tool for testing display switching
```

---

## DisplayTest (debug tool)

A separate console app that exercises the same `DisplayManager` functions:

```
dotnet run --project . -- DisplayTest list       # list displays + CCD path table
dotnet run --project . -- DisplayTest on 3       # enable display #3
dotnet run --project . -- DisplayTest off 3      # disable display #3
```

Or build it separately with `build-displaytest.bat` (no SDK required — .NET Framework only).

---

## Troubleshooting

**ADB shows orange / not reachable**
- Confirm `adb connect <TV_IP>` works in a terminal.
- Check the ADB path in registry (`HKCU\Software\SteamTV\AdbPath`).
- Make sure the TV accepted the ADB authorisation popup.

**Wrong display enabled**
- Use the **Displays…** button to see which number is your TV.
- The number matches `\\.\DISPLAYn` in Windows Display Settings.

**Gamepad not detected**
- The app looks for HID devices with *Generic Desktop / Game Pad* usage (`UP:0001_U:0005`).
- Some controllers register as joysticks (different HID usage) and won't appear. Try the **Gamepads → Refresh** list to confirm.
