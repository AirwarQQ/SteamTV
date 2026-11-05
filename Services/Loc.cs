// UI language: English and Russian. XAML takes its text through {l:Tr Key}, which binds to the
// indexer below, so switching the language re-renders every label at once; code uses Loc.T().
// Diagnostic text (the log, Test results) stays English on purpose — it's what helps with support.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;

namespace NekoCouch
{
    public sealed class Loc : INotifyPropertyChanged
    {
        public static readonly Loc Instance = new Loc();
        public static readonly string[] Languages = { "en", "ru" };

        private string _lang = "en";
        public string Language => _lang;

        public event PropertyChangedEventHandler PropertyChanged;
        public static event Action Changed;

        // "" = follow Windows.
        public static void Set(string lang)
        {
            if (string.IsNullOrEmpty(lang)) lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            if (Array.IndexOf(Languages, lang) < 0) lang = "en";
            if (lang == Instance._lang) return;
            Instance._lang = lang;
            Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs("Item[]"));
            Changed?.Invoke();
        }

        public string this[string key] => T(key);

        public static string T(string key)
        {
            string s;
            var table = Instance._lang == "ru" ? Ru : En;
            if (table.TryGetValue(key, out s) || En.TryGetValue(key, out s)) return s;
            return key;
        }

        public static string T(string key, params object[] args) => string.Format(T(key), args);

        private static readonly Dictionary<string, string> En = new Dictionary<string, string>
        {
            // Navigation
            ["Nav.Home"] = "Home", ["Nav.Devices"] = "Devices", ["Nav.Settings"] = "Settings",
            ["Nav.HomeAssistant"] = "Home Assistant", ["Nav.Diagnostics"] = "Diagnostics", ["Nav.Support"] = "Support NekoCouch",
            ["Nav.TvOk"] = "TV connected", ["Nav.TvBad"] = "TV not reachable", ["Nav.TvUnknown"] = "Checking the TV…", ["Nav.TvNone"] = "No TV set up",

            // Home
            ["Home.Kana.Morning"] = "おはよう · COUCH MODE", ["Home.Kana.Day"] = "こんにちは · COUCH MODE", ["Home.Kana.Evening"] = "こんばんは · COUCH MODE",
            ["Home.Title.Ready"] = "Ready when you are", ["Home.Title.Active"] = "Enjoy the game", ["Home.Title.Paused"] = "Taking a break", ["Home.Title.Setup"] = "Let's set up your TV",
            ["Home.Text.Ready"] = "Turn on {0} and the TV wakes up, switches to the PC and opens Big Picture.",
            ["Home.Text.ReadyNoDevice"] = "Pick a controller on the Devices page — when it connects, the TV wakes up and opens Big Picture.",
            ["Home.Text.Active"] = "Steam is on the TV. Close Big Picture, or press Back to PC, to get your monitors back.",
            ["Home.Text.Paused"] = "Monitoring is off, so controllers won't turn the TV on. Switch Monitoring on below — or press Play on TV to start right now.",
            ["Home.Text.Setup"] = "NekoCouch finds the TV on your network and fills everything in. Turn on USB or network debugging on the TV first.",
            ["Home.Status.Waiting"] = "Waiting for a controller", ["Home.Status.Active"] = "TV is on", ["Home.Status.Paused"] = "Monitoring off", ["Home.Status.Setup"] = "Not set up",
            ["Home.Play"] = "Play on TV", ["Home.Back"] = "Back to PC", ["Home.Setup"] = "Set up",
            ["Home.Play.Tip"] = "Wake the TV, switch it to the PC and open Big Picture — without waiting for a controller.",
            ["Home.Back.Tip"] = "Close Big Picture, bring your monitors and sound back and return the TV to what it showed. Also fixes things if they got out of sync.",
            ["Home.Monitoring"] = "Monitoring", ["Home.Monitoring.Sub"] = "Follows your controllers and Big Picture",
            ["Home.Triggers.None"] = "No trigger device yet", ["Home.Triggers.NoneSub"] = "Pick one on the Devices page",
            ["Home.Triggers.More"] = "and {0} more trigger device(s)", ["Home.Triggers.One"] = "Turns the TV on when it connects",
            ["Home.Tv.None"] = "No TV yet", ["Home.Tv.NoneSub"] = "Set it up on the Settings page",
            ["Home.Tv.Sub"] = "{0} · HDMI {1}", ["Home.Recent"] = "Recent", ["Home.Recent.Empty"] = "Nothing yet.",
            ["Chip.Connected"] = "Connected", ["Chip.Offline"] = "Offline", ["Chip.Unreachable"] = "Not reachable", ["Chip.Manage"] = "Manage",

            // Devices
            ["Dev.Triggers"] = "Trigger devices", ["Dev.Triggers.Sub"] = "The TV turns on when a ticked one connects.",
            ["Dev.Col.On"] = "On", ["Dev.Col.Device"] = "Device", ["Dev.Col.Category"] = "Category", ["Dev.Col.Status"] = "Status", ["Dev.Col.Bus"] = "Bus",
            ["Dev.OnTip"] = "Only ticked devices trigger the TV",
            ["Dev.NoneTicked"] = "No ticked devices — add one from the list below.", ["Dev.SelectHint"] = "Select a device above to rename it or choose what it does.",
            ["Dev.Name"] = "Name", ["Dev.NameTip"] = "Your own name for this device — leave empty to use the product name",
            ["Dev.OnConnect"] = "On connect", ["Dev.Action.Full"] = "TV + Big Picture", ["Dev.Action.TvOnly"] = "TV only",
            ["Dev.Action.FullTip"] = "Full sequence: wake TV, switch displays and HDMI input, launch Big Picture",
            ["Dev.Action.TvOnlyTip"] = "Wake TV and switch displays/HDMI input, but don't launch Big Picture",
            ["Dev.Restore"] = "Back to PC when it disconnects (after {0} s — set in Settings)",
            ["Dev.RestoreTip"] = "Once this device has been gone for 10 seconds: close Big Picture, restore the monitor layout and return the TV. Handy with 'TV only', which has no Big Picture to close.",
            ["Dev.Remove"] = "Remove", ["Dev.OnPc"] = "Devices on this PC", ["Dev.OnPc.Hint"] = "Updates live. Click a header to sort; double-click a row to add it.",
            ["Dev.Add"] = "Add", ["Dev.AllCategories"] = "All categories", ["Dev.Connected"] = "● Connected", ["Dev.Offline"] = "○ Offline",
            ["Cat.Gamepad"] = "Gamepad", ["Cat.Keyboard"] = "Keyboard", ["Cat.Keyboard + Mouse"] = "Keyboard + Mouse", ["Cat.Mouse"] = "Mouse",
            ["Cat.Audio"] = "Audio", ["Cat.Camera"] = "Camera", ["Cat.Storage"] = "Storage", ["Cat.Other"] = "Other",

            // Settings
            ["Set.Setup"] = "Set up automatically", ["Set.Setup.Sub"] = "Find the TV, connect, and fill in everything below.", ["Set.FindTv"] = "Find TV",
            ["Set.Tv"] = "TV", ["Set.Address"] = "TV address", ["Set.Hdmi"] = "HDMI input", ["Set.Hdmi.Sub"] = "The TV input the PC is plugged into. Nothing needs to be installed on the TV.",
            ["Set.Wake"] = "Wake the TV", ["Set.Wake.Sub"] = "Turn its screen on — with Wake-on-LAN if it's in deep sleep.",
            ["Set.Switch"] = "Switch the TV to the PC", ["Set.Switch.Sub"] = "Select the HDMI input, and check that it really switched.",
            ["Set.ReturnApp"] = "Afterwards, go back to the TV app", ["Set.ReturnApp.Sub"] = "Return to what the TV was showing (e.g. YouTube) instead of the home screen.",
            ["Set.SleepTv"] = "Afterwards, put the TV back to sleep", ["Set.SleepTv.Sub"] = "Only if it was off and NekoCouch woke it.",
            ["Set.Picture"] = "Picture and sound", ["Set.Display"] = "TV display", ["Set.Display.Sub"] = "The TV as Windows sees it.", ["Set.Display.Detect"] = "Pick the display whose name looks like a TV",
            ["Set.OnlyTv"] = "Only the TV while it's on", ["Set.OnlyTv.Sub"] = "Switch the other monitors off, so games started from the TV open on it and not on another monitor. The exact layout comes back afterwards.",
            ["Set.Sound"] = "Sound through the TV", ["Set.Sound.Sub"] = "Make the TV the default playback device while it's on.",
            ["Set.Hdr"] = "HDR on the TV", ["Set.Hdr.Sub"] = "Turn Windows HDR on for the TV while it's on.",
            ["Set.Steam"] = "Steam", ["Set.BigPicture"] = "Open Big Picture", ["Set.BigPicture.Sub"] = "Steam's TV mode opens on the TV, and closing it brings you back to the PC. Off: the TV just shows the PC desktop.",
            ["Set.App"] = "App", ["Set.Autostart"] = "Start with Windows", ["Set.Autostart.Sub"] = "Hidden in the tray.",
            ["Set.AutoMonitor"] = "Monitor right away", ["Set.AutoMonitor.Sub"] = "Turn monitoring on when NekoCouch starts.",
            ["Set.Language"] = "Language", ["Set.Language.Sub"] = "Applies right away.", ["Set.Language.System"] = "Same as Windows",
            ["Set.Hotkeys"] = "Keyboard shortcuts", ["Set.Hotkeys.Sub"] = "Click a field and press Ctrl, Alt or Win with a key. Escape clears it. They work while NekoCouch is in the tray.",
            ["Set.Hotkeys.On"] = "Play on TV", ["Set.Hotkeys.Off"] = "Back to PC", ["Set.Hotkeys.Save"] = "Save shortcuts",
            ["Set.About"] = "About", ["Set.Version"] = "NekoCouch {0}", ["Set.Version.Sub"] = "Free and open source. New versions are checked once a day.",
            ["Set.Support"] = "Support NekoCouch", ["Set.Support.Sub"] = "If it makes your evenings nicer, you can buy the author a coffee.", ["Set.Support.Button"] = "Support",
            ["Set.GitHub"] = "GitHub",
            ["Set.Advanced"] = "Advanced", ["Set.Advanced.Sub"] = "Paths, the TV launcher and sound device — normally filled in by themselves.",
            ["Set.Launcher"] = "TV launcher (package/.Activity)", ["Set.Launcher.Placeholder"] = "Blank = the TV's Home key", ["Set.Find"] = "Find",
            ["Set.AudioDevice"] = "TV sound device (part of its name)", ["Set.AudioDevice.Placeholder"] = "Blank = the device named after the TV display",
            ["Set.AdbHint"] = "\"adb.exe\" = the copy built into NekoCouch. Enter a path only to use your own platform-tools.",
            ["Set.Adb.Ok"] = "● Connected over ADB", ["Set.Adb.Bad"] = "● Not reachable over ADB", ["Set.Adb.Checking"] = "Checking ADB…",
            ["Set.Adb.NotSet"] = "Not set — use Set up above.", ["Set.Adb.Invalid"] = "Not a valid IP address.",
            ["Set.DisplayOff"] = " (off)", ["Set.DisplayMissing"] = "#{0} (not connected)",

            // Home Assistant
            ["Ha.Broker"] = "MQTT broker", ["Ha.Enable"] = "Connect to Home Assistant through an MQTT broker",
            ["Ha.Enable.Sub"] = "Adds a \"NekoCouch\" device to Home Assistant (MQTT discovery) with two switches: Gamepad monitoring and Steam on TV. Leave it off and nothing changes.",
            ["Ha.Host"] = "Host", ["Ha.Port"] = "Port", ["Ha.Login"] = "Login", ["Ha.Password"] = "Password",
            ["Ha.Apply"] = "Save and reconnect", ["Ha.Test"] = "Test", ["Ha.Off"] = "Off",
            ["Ha.Power"] = "PC power", ["Ha.PowerEnable"] = "Let Home Assistant put this PC to sleep, shut it down or restart it",
            ["Ha.Power.On"] = "Sleep, Shut down and Restart buttons appear on the NekoCouch device in Home Assistant. Shut down and Restart give a {0}-second Windows warning first. They only work while NekoCouch runs, i.e. after you sign in to Windows.",
            ["Ha.Power.Off"] = "Off: Home Assistant gets no power buttons, and power commands are ignored.",
            ["Ha.Wol"] = "Switching on: the PC is off then, so Home Assistant sends the Wake on LAN packet itself. Add the Wake on LAN integration there with this MAC:",
            ["Ha.MacMissing"] = "not found — see the adapter in ipconfig /all",
            ["Ha.Status.Connecting"] = "Connecting to {0}…", ["Ha.Status.Connected"] = "Connected to {0}",
            ["Ha.Status.Retrying"] = "Not connected: {0} (retrying)", ["Ha.NeedHost"] = "Not connected: enter the broker host",

            // Diagnostics
            ["Diag.Steps"] = "Try one step at a time", ["Diag.Steps.Sub"] = "Each runs in the background and writes its result to the log.",
            ["Diag.Log"] = "Log", ["Diag.OpenLog"] = "Open log file",

            // Setup (wizard)
            ["Setup.Looking"] = "Looking for Android TVs on the network…",
            ["Setup.NoneFound"] = "No Android TV found. On the TV: Developer options → turn on USB debugging / network debugging, and make sure it's on the same network.",
            ["Setup.FoundOne"] = "Found {0}.", ["Setup.FoundMany"] = "Found {0} devices — pick the TV in the list, then click Set up again.",
            ["Setup.Connecting"] = "Connecting to {0}…", ["Setup.Accept"] = "On the TV, accept \"Allow USB debugging?\" (tick \"Always allow\")… {0} s",
            ["Setup.Denied"] = "The TV didn't allow the connection ({0}). Accept the prompt on the TV, then click Set up again.", ["Setup.NoAnswer"] = "no answer",
            ["Setup.Done"] = "Done: {0} at {1} · {2} · display {3} · launcher {4} · Steam {5}. Next: tick your controller on the Devices page.",
            ["Setup.Port.One"] = "HDMI {0}", ["Setup.Port.None"] = "HDMI {0} (the TV reports no connected input — check it)",
            ["Setup.Port.Many"] = "HDMI {0} (cables in {1} — change it if the PC is on another)",
            ["Setup.Display.Missing"] = "not found — pick it in Settings", ["Setup.HomeKey"] = "Home key",
            ["Setup.Steam.Found"] = "found", ["Setup.Steam.Missing"] = "not found — set its path in Settings → Advanced",
            ["Setup.Failed"] = "Setup failed: {0}", ["Setup.NoTvDisplay"] = "No display named like a TV — pick it from the list.",
            ["Setup.TvDisplay"] = "TV display: {0}.",

            // Tray
            ["Tray.Play"] = "Play on TV", ["Tray.Back"] = "Back to PC", ["Tray.Monitoring"] = "Monitoring", ["Tray.Show"] = "Open NekoCouch",
            ["Tray.Autostart"] = "Start with Windows", ["Tray.AutoMonitor"] = "Monitor right away", ["Tray.Update"] = "Install update {0}",
            ["Tray.Log"] = "Open log file", ["Tray.Support"] = "Support NekoCouch", ["Tray.Exit"] = "Exit",
            ["Tray.Status.Active"] = "TV is on", ["Tray.Status.Waiting"] = "Waiting for a controller", ["Tray.Status.Paused"] = "Monitoring off",
            ["Tray.Tip"] = "NekoCouch — {0} · TV {1}", ["Tray.Tip.Ok"] = "reachable", ["Tray.Tip.Bad"] = "not reachable", ["Tray.Tip.Checking"] = "checking…",

            // Notifications and dialogs
            ["Msg.NoTv"] = "No TV set yet — click Set up on the Home page.", ["Msg.BadIp"] = "That isn't a valid IP address.",
            ["Msg.Ignored"] = "Command ignored: {0}",
            ["Msg.NewIp"] = "The TV got a new IP address ({0}) — settings updated.",
            ["Msg.AdbLost"] = "The TV hasn't answered over ADB for a while — check that network debugging is still on in its Developer options (some TVs turn it off after a reboot).",
            ["Msg.HotkeyChange"] = "Change it in Settings.",
            ["Msg.UpdateTitle"] = "NekoCouch update available", ["Msg.UpdateText"] = "Version {0} is out — click to install it.",
            ["Msg.UpdateAsk"] = "Install NekoCouch {0} now? NekoCouch restarts; an active TV session is restored first.",
            ["Msg.UpdateFailed"] = "Update failed: {0}\n\nOpen the releases page?",
            ["Phase.Start"] = "Getting the TV ready…", ["Phase.Wake"] = "Waking the TV…", ["Phase.Display"] = "Turning the TV display on…",
            ["Phase.Hdmi"] = "Switching the TV to HDMI {0}…", ["Phase.Sound"] = "Moving the sound to the TV…", ["Phase.BigPicture"] = "Opening Big Picture…",
            ["Phase.Back"] = "Bringing everything back to the PC…",
            ["Home.Title.Busy"] = "One moment…", ["Home.Title.Returning"] = "Back to the PC…", ["Home.Status.Busy"] = "Working",
            ["Home.Title.Unreachable"] = "Can't see the TV", ["Home.Status.Unreachable"] = "TV not reachable",
            ["Home.Text.Unreachable"] = "It isn't answering at {0}. Is it on the network, with network debugging still on? NekoCouch keeps checking.",
            ["Home.Tip.Key"] = "Shortcut: {0}",
            ["Set.MinimizeSteam"] = "Minimize Steam afterwards", ["Set.MinimizeSteam.Sub"] = "Once you're back on the PC, Steam's window gets out of the way.",
            ["Set.Grace"] = "Back to PC after a controller disconnects", ["Set.Grace.Sub"] = "For devices that have it turned on. Rides out short Bluetooth dropouts.",
            ["Set.Seconds"] = "{0} s",
            ["Set.PerDevice"] = "Which devices start the TV, and whether disconnecting one ends the session, is set per device.", ["Set.PerDevice.Button"] = "Devices",
            ["Set.Hotkeys.Toggle"] = "One shortcut for both — Play on TV when the TV is off, Back to PC when it's on", ["Set.Hotkeys.Both"] = "TV on / off",
            ["Set.CheckUpdates"] = "Check for updates", ["Upd.Latest"] = "You have the latest version.", ["Upd.Failed"] = "Couldn't reach GitHub to check for updates.",
            ["Tray.CheckUpdates"] = "Check for updates",
            ["Set.Notify"] = "Windows notifications", ["Set.Notify.Sub"] = "Pop-ups when the TV can't be reached, its address changes or an update is out. Off: only the log and the Home page say so.",
            ["Set.Animate"] = "Animated Home banner", ["Set.Animate.Sub"] = "Falling petals, clouds and the swaying branch. Off: a still picture and no work for the CPU.",
            ["Upd.Checked"] = "Checked at {0} — you have the latest version.", ["Upd.CheckedFail"] = "Checked at {0} — couldn't reach GitHub.", ["Upd.Checking"] = "Checking…",
            ["Note.BpNotClosed"] = "Couldn't close Big Picture before restoring the monitors.",
            ["Note.RestoreFailed"] = "Couldn't restore the monitor layout: {0}",
            ["Note.Unreachable"] = "Can't reach the TV over ADB ({0}). Is it on the network, with network debugging on?",
            ["Note.BpFailed"] = "Steam Big Picture didn't start: {0}",
            ["Note.SwitchFailed"] = "Can't switch the TV's HDMI input — rolled back. {0}",
            ["Note.SwitchTimeout"] = "The TV didn't switch to the PC's HDMI input in time — rolled back.",
            ["Hot.NeedMod"] = "Include Ctrl, Alt or Win in the shortcut.", ["Hot.PressSave"] = "Press Save shortcuts to apply.", ["Hot.Saved"] = "Shortcuts saved.",
            ["Launcher.Looking"] = "Looking for the TV's default Home app…", ["Launcher.Saved"] = "Launcher saved: {0}",
            ["Launcher.IpChanged"] = "The TV address changed. Search again for the selected TV.",
        };

        private static readonly Dictionary<string, string> Ru = new Dictionary<string, string>
        {
            ["Nav.Home"] = "Главная", ["Nav.Devices"] = "Устройства", ["Nav.Settings"] = "Настройки",
            ["Nav.HomeAssistant"] = "Home Assistant", ["Nav.Diagnostics"] = "Диагностика", ["Nav.Support"] = "Поддержать автора",
            ["Nav.TvOk"] = "ТВ на связи", ["Nav.TvBad"] = "ТВ не отвечает", ["Nav.TvUnknown"] = "Проверяю ТВ…", ["Nav.TvNone"] = "ТВ не настроен",

            ["Home.Title.Ready"] = "Можно играть", ["Home.Title.Active"] = "Приятной игры", ["Home.Title.Paused"] = "Перерыв", ["Home.Title.Setup"] = "Настроим телевизор",
            ["Home.Text.Ready"] = "Включи {0} — телевизор проснётся, переключится на ПК и откроет Big Picture.",
            ["Home.Text.ReadyNoDevice"] = "Выбери геймпад на странице «Устройства» — как только он подключится, телевизор проснётся и откроет Big Picture.",
            ["Home.Text.Active"] = "Steam на телевизоре. Закрой Big Picture или нажми «Вернуться к ПК», чтобы вернуть мониторы.",
            ["Home.Text.Paused"] = "Мониторинг выключен, поэтому геймпады не включают телевизор. Включи «Мониторинг» ниже — или нажми «Играть на ТВ», чтобы начать прямо сейчас.",
            ["Home.Text.Setup"] = "NekoCouch сам найдёт телевизор в сети и всё заполнит. Сначала включи на телевизоре отладку по USB или по сети.",
            ["Home.Status.Waiting"] = "Жду геймпад", ["Home.Status.Active"] = "Телевизор включён", ["Home.Status.Paused"] = "Мониторинг выключен", ["Home.Status.Setup"] = "Не настроено",
            ["Home.Play"] = "Играть на ТВ", ["Home.Back"] = "Вернуться к ПК", ["Home.Setup"] = "Настроить",
            ["Home.Play.Tip"] = "Разбудить телевизор, переключить на ПК и открыть Big Picture — не дожидаясь геймпада.",
            ["Home.Back.Tip"] = "Закрыть Big Picture, вернуть мониторы и звук, а телевизор — к тому, что на нём было. Заодно чинит, если что-то рассинхронизировалось.",
            ["Home.Monitoring"] = "Мониторинг", ["Home.Monitoring.Sub"] = "Следит за геймпадами и Big Picture",
            ["Home.Triggers.None"] = "Геймпад не выбран", ["Home.Triggers.NoneSub"] = "Выбери его на странице «Устройства»",
            ["Home.Triggers.More"] = "и ещё устройств: {0}", ["Home.Triggers.One"] = "Включает телевизор при подключении",
            ["Home.Tv.None"] = "Телевизора нет", ["Home.Tv.NoneSub"] = "Настрой его в «Настройках»",
            ["Home.Tv.Sub"] = "{0} · HDMI {1}", ["Home.Recent"] = "Недавнее", ["Home.Recent.Empty"] = "Пока ничего.",
            ["Chip.Connected"] = "На связи", ["Chip.Offline"] = "Выключен", ["Chip.Unreachable"] = "Не отвечает", ["Chip.Manage"] = "Управлять",

            ["Dev.Triggers"] = "Устройства-триггеры", ["Dev.Triggers.Sub"] = "Телевизор включается, когда подключается отмеченное устройство.",
            ["Dev.Col.On"] = "Вкл", ["Dev.Col.Device"] = "Устройство", ["Dev.Col.Category"] = "Тип", ["Dev.Col.Status"] = "Статус", ["Dev.Col.Bus"] = "Шина",
            ["Dev.OnTip"] = "Телевизор включают только отмеченные устройства",
            ["Dev.NoneTicked"] = "Нет отмеченных устройств — добавь одно из списка ниже.", ["Dev.SelectHint"] = "Выбери устройство выше, чтобы переименовать его или выбрать, что оно делает.",
            ["Dev.Name"] = "Имя", ["Dev.NameTip"] = "Своё имя для устройства — пусто = название производителя",
            ["Dev.OnConnect"] = "При подключении", ["Dev.Action.Full"] = "ТВ + Big Picture", ["Dev.Action.TvOnly"] = "Только ТВ",
            ["Dev.Action.FullTip"] = "Всё сразу: разбудить ТВ, переключить экраны и HDMI, открыть Big Picture",
            ["Dev.Action.TvOnlyTip"] = "Разбудить ТВ и переключить экраны и HDMI, но без Big Picture",
            ["Dev.Restore"] = "Вернуться к ПК, когда оно отключится (через {0} с — задаётся в Настройках)",
            ["Dev.RestoreTip"] = "Когда устройства нет уже 10 секунд: закрыть Big Picture, вернуть мониторы и телевизор. Удобно с «Только ТВ», где нечего закрывать.",
            ["Dev.Remove"] = "Убрать", ["Dev.OnPc"] = "Устройства на этом ПК", ["Dev.OnPc.Hint"] = "Обновляется само. Клик по заголовку сортирует, двойной клик по строке добавляет.",
            ["Dev.Add"] = "Добавить", ["Dev.AllCategories"] = "Все типы", ["Dev.Connected"] = "● Подключено", ["Dev.Offline"] = "○ Выключено",
            ["Cat.Gamepad"] = "Геймпад", ["Cat.Keyboard"] = "Клавиатура", ["Cat.Keyboard + Mouse"] = "Клавиатура и мышь", ["Cat.Mouse"] = "Мышь",
            ["Cat.Audio"] = "Звук", ["Cat.Camera"] = "Камера", ["Cat.Storage"] = "Накопитель", ["Cat.Other"] = "Другое",

            ["Set.Setup"] = "Настроить автоматически", ["Set.Setup.Sub"] = "Найти телевизор, подключиться и заполнить всё ниже.", ["Set.FindTv"] = "Найти ТВ",
            ["Set.Tv"] = "Телевизор", ["Set.Address"] = "Адрес телевизора", ["Set.Hdmi"] = "Вход HDMI", ["Set.Hdmi.Sub"] = "Вход телевизора, к которому подключён ПК. Ставить на ТВ ничего не нужно.",
            ["Set.Wake"] = "Будить телевизор", ["Set.Wake.Sub"] = "Включать экран — через Wake-on-LAN, если ТВ в глубоком сне.",
            ["Set.Switch"] = "Переключать ТВ на ПК", ["Set.Switch.Sub"] = "Выбрать вход HDMI и проверить, что он правда переключился.",
            ["Set.ReturnApp"] = "Потом вернуться в приложение ТВ", ["Set.ReturnApp.Sub"] = "К тому, что было открыто (например, YouTube), а не на главный экран.",
            ["Set.SleepTv"] = "Потом снова усыпить телевизор", ["Set.SleepTv.Sub"] = "Только если он был выключен и NekoCouch его разбудил.",
            ["Set.Picture"] = "Картинка и звук", ["Set.Display"] = "Экран телевизора", ["Set.Display.Sub"] = "Телевизор, каким его видит Windows.", ["Set.Display.Detect"] = "Выбрать экран, чьё имя похоже на телевизор",
            ["Set.OnlyTv"] = "Только телевизор, пока он включён", ["Set.OnlyTv.Sub"] = "Выключать остальные мониторы, чтобы игры, запущенные с телевизора, открывались на нём, а не на другом мониторе. Раскладка потом вернётся в точности.",
            ["Set.Sound"] = "Звук через телевизор", ["Set.Sound.Sub"] = "Делать ТВ устройством вывода по умолчанию, пока он включён.",
            ["Set.Hdr"] = "HDR на телевизоре", ["Set.Hdr.Sub"] = "Включать HDR Windows для телевизора, пока он включён.",
            ["Set.Steam"] = "Steam", ["Set.BigPicture"] = "Открывать Big Picture", ["Set.BigPicture.Sub"] = "На телевизоре открывается режим Steam для ТВ, а его закрытие возвращает к ПК. Выключено: телевизор просто показывает рабочий стол ПК.",
            ["Set.App"] = "Приложение", ["Set.Autostart"] = "Запускать вместе с Windows", ["Set.Autostart.Sub"] = "Сразу в трей.",
            ["Set.AutoMonitor"] = "Сразу включать мониторинг", ["Set.AutoMonitor.Sub"] = "Когда NekoCouch запускается.",
            ["Set.Language"] = "Язык", ["Set.Language.Sub"] = "Меняется сразу.", ["Set.Language.System"] = "Как в Windows",
            ["Set.Hotkeys"] = "Горячие клавиши", ["Set.Hotkeys.Sub"] = "Кликни в поле и нажми Ctrl, Alt или Win с клавишей. Escape очищает. Работают, даже когда NekoCouch в трее.",
            ["Set.Hotkeys.On"] = "Играть на ТВ", ["Set.Hotkeys.Off"] = "Вернуться к ПК", ["Set.Hotkeys.Save"] = "Сохранить",
            ["Set.About"] = "О программе", ["Set.Version"] = "NekoCouch {0}", ["Set.Version.Sub"] = "Бесплатно и с открытым кодом. Новые версии проверяются раз в день.",
            ["Set.Support"] = "Поддержать NekoCouch", ["Set.Support.Sub"] = "Если с ним вечера стали уютнее — можно угостить автора кофе.", ["Set.Support.Button"] = "Поддержать",
            ["Set.GitHub"] = "GitHub",
            ["Set.Advanced"] = "Дополнительно", ["Set.Advanced.Sub"] = "Пути, лаунчер телевизора и звуковое устройство — обычно заполняются сами.",
            ["Set.Launcher"] = "Лаунчер телевизора (package/.Activity)", ["Set.Launcher.Placeholder"] = "Пусто = кнопка Home телевизора", ["Set.Find"] = "Найти",
            ["Set.AudioDevice"] = "Звуковое устройство ТВ (часть имени)", ["Set.AudioDevice.Placeholder"] = "Пусто = устройство с именем экрана ТВ",
            ["Set.AdbHint"] = "«adb.exe» = копия, встроенная в NekoCouch. Путь нужен, только если хочешь свои platform-tools.",
            ["Set.Adb.Ok"] = "● На связи по ADB", ["Set.Adb.Bad"] = "● Не отвечает по ADB", ["Set.Adb.Checking"] = "Проверяю ADB…",
            ["Set.Adb.NotSet"] = "Не задан — нажми «Настроить» выше.", ["Set.Adb.Invalid"] = "Это не IP-адрес.",
            ["Set.DisplayOff"] = " (выкл)", ["Set.DisplayMissing"] = "#{0} (не подключён)",

            ["Ha.Broker"] = "MQTT-брокер", ["Ha.Enable"] = "Подключаться к Home Assistant через MQTT-брокер",
            ["Ha.Enable.Sub"] = "Добавляет в Home Assistant устройство «NekoCouch» (MQTT discovery) с двумя переключателями: мониторинг геймпадов и Steam на ТВ. Выключено — ничего не меняется.",
            ["Ha.Host"] = "Хост", ["Ha.Port"] = "Порт", ["Ha.Login"] = "Логин", ["Ha.Password"] = "Пароль",
            ["Ha.Apply"] = "Сохранить и переподключить", ["Ha.Test"] = "Проверить", ["Ha.Off"] = "Выключено",
            ["Ha.Power"] = "Питание ПК", ["Ha.PowerEnable"] = "Разрешить Home Assistant усыплять, выключать и перезагружать этот ПК",
            ["Ha.Power.On"] = "У устройства NekoCouch в Home Assistant появятся кнопки «Сон», «Выключить» и «Перезагрузить». Перед выключением и перезагрузкой Windows предупреждает за {0} с. Работают, только пока запущен NekoCouch, то есть после входа в Windows.",
            ["Ha.Power.Off"] = "Выключено: кнопок питания в Home Assistant нет, команды питания игнорируются.",
            ["Ha.Wol"] = "Включение: ПК в этот момент выключен, поэтому пакет Wake on LAN шлёт сам Home Assistant. Добавь там интеграцию Wake on LAN с этим MAC:",
            ["Ha.MacMissing"] = "не найден — посмотри адаптер в ipconfig /all",
            ["Ha.Status.Connecting"] = "Подключаюсь к {0}…", ["Ha.Status.Connected"] = "Подключено к {0}",
            ["Ha.Status.Retrying"] = "Нет подключения: {0} (пробую снова)", ["Ha.NeedHost"] = "Нет подключения: укажи хост брокера",

            ["Diag.Steps"] = "Проверить по шагу", ["Diag.Steps.Sub"] = "Каждое действие выполняется в фоне и пишет результат в лог.",
            ["Diag.Log"] = "Лог", ["Diag.OpenLog"] = "Открыть файл лога",

            ["Setup.Looking"] = "Ищу Android-телевизоры в сети…",
            ["Setup.NoneFound"] = "Android-телевизор не найден. На ТВ: «Для разработчиков» → включи отладку по USB / по сети и проверь, что он в той же сети.",
            ["Setup.FoundOne"] = "Найден {0}.", ["Setup.FoundMany"] = "Найдено устройств: {0} — выбери телевизор в списке и снова нажми «Настроить».",
            ["Setup.Connecting"] = "Подключаюсь к {0}…", ["Setup.Accept"] = "На телевизоре разреши «Отладку по USB» (отметь «Всегда разрешать»)… {0} с",
            ["Setup.Denied"] = "Телевизор не разрешил подключение ({0}). Подтверди запрос на ТВ и снова нажми «Настроить».", ["Setup.NoAnswer"] = "нет ответа",
            ["Setup.Done"] = "Готово: {0} по адресу {1} · {2} · экран {3} · лаунчер {4} · Steam {5}. Дальше: отметь свой геймпад на странице «Устройства».",
            ["Setup.Port.One"] = "HDMI {0}", ["Setup.Port.None"] = "HDMI {0} (телевизор не видит подключённых входов — проверь)",
            ["Setup.Port.Many"] = "HDMI {0} (кабели в {1} — поменяй, если ПК в другом)",
            ["Setup.Display.Missing"] = "не найден — выбери в «Настройках»", ["Setup.HomeKey"] = "кнопка Home",
            ["Setup.Steam.Found"] = "найден", ["Setup.Steam.Missing"] = "не найден — укажи путь в «Настройки → Дополнительно»",
            ["Setup.Failed"] = "Настройка не удалась: {0}", ["Setup.NoTvDisplay"] = "Нет экрана с именем, похожим на телевизор — выбери его в списке.",
            ["Setup.TvDisplay"] = "Экран телевизора: {0}.",

            ["Tray.Play"] = "Играть на ТВ", ["Tray.Back"] = "Вернуться к ПК", ["Tray.Monitoring"] = "Мониторинг", ["Tray.Show"] = "Открыть NekoCouch",
            ["Tray.Autostart"] = "Запускать с Windows", ["Tray.AutoMonitor"] = "Сразу включать мониторинг", ["Tray.Update"] = "Установить обновление {0}",
            ["Tray.Log"] = "Открыть файл лога", ["Tray.Support"] = "Поддержать NekoCouch", ["Tray.Exit"] = "Выход",
            ["Tray.Status.Active"] = "Телевизор включён", ["Tray.Status.Waiting"] = "Жду геймпад", ["Tray.Status.Paused"] = "Мониторинг выключен",
            ["Tray.Tip"] = "NekoCouch — {0} · ТВ {1}", ["Tray.Tip.Ok"] = "на связи", ["Tray.Tip.Bad"] = "не отвечает", ["Tray.Tip.Checking"] = "проверяю…",

            ["Msg.NoTv"] = "Телевизор ещё не настроен — нажми «Настроить» на главной.", ["Msg.BadIp"] = "Это не IP-адрес.",
            ["Msg.Ignored"] = "Команда пропущена: {0}",
            ["Msg.NewIp"] = "У телевизора новый IP-адрес ({0}) — настройки обновлены.",
            ["Msg.AdbLost"] = "Телевизор давно не отвечает по ADB — проверь, что отладка по сети всё ещё включена в «Для разработчиков» (некоторые ТВ выключают её после перезагрузки).",
            ["Msg.HotkeyChange"] = "Поменяй его в «Настройках».",
            ["Msg.UpdateTitle"] = "Вышло обновление NekoCouch", ["Msg.UpdateText"] = "Вышла версия {0} — нажми, чтобы установить.",
            ["Msg.UpdateAsk"] = "Установить NekoCouch {0} сейчас? NekoCouch перезапустится; активная сессия на ТВ сначала завершится.",
            ["Msg.UpdateFailed"] = "Обновление не удалось: {0}\n\nОткрыть страницу релизов?",
            ["Phase.Start"] = "Готовлю телевизор…", ["Phase.Wake"] = "Бужу телевизор…", ["Phase.Display"] = "Включаю экран телевизора…",
            ["Phase.Hdmi"] = "Переключаю телевизор на HDMI {0}…", ["Phase.Sound"] = "Переношу звук на телевизор…", ["Phase.BigPicture"] = "Открываю Big Picture…",
            ["Phase.Back"] = "Возвращаю всё на ПК…",
            ["Home.Title.Busy"] = "Секунду…", ["Home.Title.Returning"] = "Возвращаюсь к ПК…", ["Home.Status.Busy"] = "Работаю",
            ["Home.Title.Unreachable"] = "Не вижу телевизор", ["Home.Status.Unreachable"] = "ТВ не отвечает",
            ["Home.Text.Unreachable"] = "Он не отвечает по адресу {0}. Телевизор в сети и отладка по сети всё ещё включена? NekoCouch продолжает проверять.",
            ["Home.Tip.Key"] = "Горячая клавиша: {0}",
            ["Set.MinimizeSteam"] = "Потом сворачивать Steam", ["Set.MinimizeSteam.Sub"] = "Когда вернёшься к ПК, окно Steam не будет мешать.",
            ["Set.Grace"] = "Вернуться к ПК после отключения геймпада", ["Set.Grace.Sub"] = "Для устройств, у которых это включено. Пережидает короткие обрывы Bluetooth.",
            ["Set.Seconds"] = "{0} с",
            ["Set.PerDevice"] = "Какие устройства включают телевизор и завершает ли сессию их отключение — задаётся для каждого устройства.", ["Set.PerDevice.Button"] = "Устройства",
            ["Set.Hotkeys.Toggle"] = "Одна клавиша на оба действия: «Играть на ТВ», когда ТВ выключен, и «Вернуться к ПК», когда включён", ["Set.Hotkeys.Both"] = "ТВ вкл / выкл",
            ["Set.CheckUpdates"] = "Проверить обновления", ["Upd.Latest"] = "У тебя последняя версия.", ["Upd.Failed"] = "Не получилось связаться с GitHub, чтобы проверить обновления.",
            ["Tray.CheckUpdates"] = "Проверить обновления",
            ["Set.Notify"] = "Уведомления Windows", ["Set.Notify.Sub"] = "Всплывают, если телевизор недоступен, сменил адрес или вышло обновление. Выключено: об этом пишут только журнал и главная.",
            ["Set.Animate"] = "Анимация на главной", ["Set.Animate.Sub"] = "Падающие лепестки, облака и качающаяся ветка. Выключено: неподвижная картинка и никакой нагрузки на процессор.",
            ["Upd.Checked"] = "Проверено в {0} — у тебя последняя версия.", ["Upd.CheckedFail"] = "Проверено в {0} — не удалось связаться с GitHub.", ["Upd.Checking"] = "Проверяю…",
            ["Note.BpNotClosed"] = "Не получилось закрыть Big Picture перед возвратом мониторов.",
            ["Note.RestoreFailed"] = "Не получилось вернуть раскладку мониторов: {0}",
            ["Note.Unreachable"] = "Телевизор не отвечает по ADB ({0}). Он в сети и отладка по сети включена?",
            ["Note.BpFailed"] = "Steam Big Picture не запустился: {0}",
            ["Note.SwitchFailed"] = "Не получилось переключить вход HDMI — всё возвращено. {0}",
            ["Note.SwitchTimeout"] = "Телевизор не переключился на вход ПК вовремя — всё возвращено.",
            ["Hot.NeedMod"] = "Добавь в сочетание Ctrl, Alt или Win.", ["Hot.PressSave"] = "Нажми «Сохранить», чтобы применить.", ["Hot.Saved"] = "Сочетания сохранены.",
            ["Launcher.Looking"] = "Ищу лаунчер телевизора…", ["Launcher.Saved"] = "Лаунчер сохранён: {0}",
            ["Launcher.IpChanged"] = "Адрес телевизора изменился. Поищи ещё раз для выбранного ТВ.",
        };
    }

    // {l:Tr Key} in XAML: a one-way binding to Loc.Instance[Key] that follows language changes.
    [MarkupExtensionReturnType(typeof(object))]
    public sealed class TrExtension : MarkupExtension
    {
        public TrExtension() { }
        public TrExtension(string key) { Key = key; }

        [ConstructorArgument("key")]
        public string Key { get; set; }

        public override object ProvideValue(IServiceProvider serviceProvider) =>
            new Binding("[" + Key + "]") { Source = Loc.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
    }
}
