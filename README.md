# SteamTV

Windows utility that watches for a gamepad (or any other device you choose — keyboard, mouse, headset…) to connect and automatically switches your TV to PC input, enables the TV display in Windows, and launches Steam Big Picture. When Big Picture is closed, it restores your monitor layout and switches the TV back to its home screen.

Originally a PowerShell script, rewritten in C# with a proper UI.

---

## How it works

1. **A ticked trigger device connects** (USB or Bluetooth) → wake TV via ADB, enable TV display in Windows, disable other monitors (optional), switch TV HDMI input to PC, launch Steam Big Picture.
2. **Big Picture closes** (or, optionally, the trigger device disconnects) → restore monitor layout, switch TV back to home screen, minimize Steam.

---

## Requirements

| Requirement | Notes |
|---|---|
| **Windows 10 / 11** | — |
| **.NET Framework 4.8** | Pre-installed on Windows 10 1903+ and all Windows 11. [Download](https://dotnet.microsoft.com/download/dotnet-framework/net48) if missing. |
| **Android Debug Bridge (`adb.exe`)** | Bundled — extracted next to `SteamTV.exe` on first run, nothing to install. Set a custom path on the Advanced tab if you'd rather use your own. |
| **Steam** | Standard install at `C:\Program Files (x86)\Steam\steam.exe` or configure the path in settings. |
| **TV with ADB over Wi-Fi** | Enable *Developer options → USB debugging* (or *Wireless debugging*) on your TV, then allow the ADB connection from your PC. |
| **TV External Source app** | [com.liskovsoft.tvexternalsource](https://github.com/yuliskov/SmartTubeNext) — installed on the TV; used to switch HDMI inputs via `adb shell monkey`. |

---

## Setup

### 1. Enable ADB on your TV
- Go to **Settings → Device Preferences → About → Build number** — tap 7 times to unlock Developer Options.
- In **Developer Options**, enable **USB Debugging** (ADB).
- Find your TV's IP address under **Settings → Network**.

### 2. Build
```
dotnet build -c Release
```
Output: `bin\Release\net48\SteamTV.exe` — a single self-contained exe (WPF-UI and `adb.exe` are both merged in at build time), nothing else to copy alongside it. `adb.exe` is written back out next to `SteamTV.exe` the first time you run it.

Or open in Visual Studio 2022+ and press **Build**.

### 3. Pair adb with the TV (first time only)
Run the app once so it extracts `adb.exe` next to itself, then from that folder:
```
adb connect <TV_IP>
```
Accept the connection prompt on the TV. (You can also just start monitoring in the app — it runs the same `connect` call itself and the TV will show the same prompt the first time.)

### 4. Configure in the app
Open **SteamTV.exe** and fill in the **Monitor** tab:

| Field | Description |
|---|---|
| **TV IP** | IP address of your TV on the local network |
| **HDMI port** | Port number your PC is connected to (1–4); used by the TV External Source app |
| **Target display #** | Windows display number for the TV — click **Displays…** to find it |

Click **Start** to begin monitoring. The ADB indicator under the TV IP field shows reachability, refreshed every 15 s.

Light / Dark / Auto theme switcher is in the title bar (top-right); Auto follows the Windows theme and updates live if you change it.

### 5. Pick your trigger devices
Go to the **Devices** tab. The bottom list shows every external USB and Bluetooth device Windows knows about — with its real product name, a category (Gamepad, Keyboard, Mouse, Audio, Camera, Storage, Other) and live connection status. Click a column header to sort, or use the category filter. Select a device and click **↑ Add** (or double-click it).

Added devices appear in the top list with their live status and an **On** checkbox. Only ticked devices trigger activation — untick one to keep it on the list without it doing anything.

Activation fires when a ticked device *connects* (goes from offline to connected), so a keyboard that's always plugged in won't block a gamepad from triggering. Paired Bluetooth devices stay listed while switched off and show as **Offline**, so you can add a controller even when it isn't on. Connections are picked up the moment Windows reports them (device-change notifications), with a 2 s poll as a fallback.

Select a trigger device to change its settings:

| Setting | Effect |
|---|---|
| **Name** | Your own name for it (e.g. "Living-room pad") — shown in the list and the log. Leave empty for the product name. |
| **On connect** | **TV + Big Picture** — the full sequence. **TV only** — wake the TV, switch displays and HDMI input, but don't launch Big Picture (e.g. a keyboard for desktop use on the TV). |
| **Restore monitors when it disconnects** | Once this device has been gone for 10 s: close Big Picture if it's open, restore the monitor layout and send the TV home. The 10 s grace stops a Bluetooth controller's momentary dropout from tearing the session down. Pairs naturally with **TV only**, which has no Big Picture to close. |

While the TV is already active, another trigger device connecting doesn't re-run activation.

### 6. Autostart (optional)
On the **Monitor** tab, check **Run at Windows startup (hidden in tray)**. The app will start minimised to tray and begin monitoring automatically.

To control this from the tray icon: right-click → **Autostart app** / **Auto-start monitoring on launch**. The tray icon itself turns red when ADB can't reach the TV (green when it can), and the right-click menu shows current ADB and monitoring status at the top.

---

## Settings

All settings are stored in the registry at `HKCU\Software\SteamTV`.

`adb.exe` / `steam.exe` paths and the TV-specific HDMI activity pattern / home component are editable on the **Advanced** tab (or directly in the registry, same effect).

**Default paths:**
- `adb.exe` — the copy extracted next to `SteamTV.exe` on first run; set a full path on the Advanced tab if you'd rather use your own install (e.g. `C:\platform-tools\adb.exe`)
- `steam.exe` — `C:\Program Files (x86)\Steam\steam.exe`

**TV integration:** the default `HdmiActivityPattern` and `TvHomeComponent` on the Advanced tab match Xiaomi/MiTV's launcher. On any other TV, source-switch detection will just time out until you set both to match your own TV — see below.

### Finding your TV's values for another TV

Both values come from the same adb command, run at two different moments. With the TV connected (`adb connect <TV_IP>`):

1. **HDMI activity pattern** — on the TV, manually switch the input to the PC's HDMI port (however you'd normally do it with the remote), then run:
   ```
   adb shell dumpsys activity activities | findstr ResumedActivity
   ```
   The line looks like `... ResumedActivity{... com.example.tvplayer/.ExternalSourceActivity ...}`. Take the `package/.Activity` part and escape the dots for regex: `com\.example\.tvplayer/\.ExternalSourceActivity`. Paste that into **HDMI activity pattern**.

2. **TV home component** — put the TV back on its home screen by hand, then run the same command again:
   ```
   adb shell dumpsys activity activities | findstr ResumedActivity
   ```
   This time take the `package/.Activity` shown as-is (no escaping) — e.g. `com.example.launcher/.MainActivity` — and paste it into **TV home component**.

If `findstr` finds nothing, drop it and scroll the raw `adb shell dumpsys activity activities` output for the line containing `ResumedActivity` yourself.

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
├─ App.xaml / App.xaml.cs  — entry point (single-instance mutex, -autostart flag, WPF-UI theme)
├─ MainWindow.xaml / .cs   — tab UI: Monitor, Devices, Test, Advanced; tray icon
├─ Appsettings.cs          — registry-backed settings + WatchedDevice model
├─ app.manifest            — PerMonitorV2 DPI awareness (crisp on HiDPI displays, handled natively by WPF)
├─ Interop/
│  └─ Native.cs            — P/Invoke: user32 CCD display API, dwmapi
├─ Services/
│  ├─ Displaymanager.cs    — enable/disable/restore monitors via SetDisplayConfig
│  ├─ Adb.cs               — adb.exe wrapper (wake, source switch, reachability check)
│  ├─ AdbBundle.cs         — extracts the embedded adb.exe next to SteamTV.exe on first run
│  ├─ DeviceScanner.cs     — external USB/Bluetooth device enumeration via cfgmgr32 (names, categories, BT connection state)
│  ├─ Steamhelper.cs       — Big Picture detection, launching/minimizing Steam
│  └─ Monitorservice.cs    — background loop: reacts to trigger devices + Big Picture state
├─ Vendor/adb.exe          — bundled Android Platform Tools binary, embedded into the exe at build time
└─ Displaytest.cs          — standalone CLI tool for testing display switching
```

UI is built with [WPF-UI](https://github.com/lepoco/wpfui) (Fluent design, follows the Windows light/dark theme automatically).

---

## DisplayTest (debug tool)

A separate console app that exercises the same `DisplayManager` functions — excluded from the main build (it has its own `Main`), build it with `build-displaytest.bat` (no SDK required, just the .NET Framework compiler already on Windows):

```
DisplayTest list       # list displays + CCD path table
DisplayTest on 3       # enable display #3
DisplayTest off 3      # disable display #3
```

---

## Troubleshooting

**ADB shows orange / not reachable**
- Confirm `adb connect <TV_IP>` works in a terminal.
- Check the ADB path in registry (`HKCU\Software\SteamTV\AdbPath`).
- Make sure the TV accepted the ADB authorisation popup.

**Wrong display enabled**
- Use the **Displays…** button to see which number is your TV.
- The number matches `\\.\DISPLAYn` in Windows Display Settings.

**Device not in the list**
- Only external devices are listed — anything Windows reports as built into the PC (internal webcam, laptop keyboard, Bluetooth radio) and USB hubs are filtered out.
- Bluetooth devices must be paired in Windows first; once paired they stay listed (as Offline) even while switched off.
- Gamepads are recognized by HID game pad / joystick usage, plus Xbox controllers by their XUSB/GIP interface. Anything else still shows up, just under **Other**.
