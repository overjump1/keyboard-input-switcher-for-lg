using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LgInputSwitcher.Tests
{
    // Records switches instead of talking to a monitor, so tests never change the real LG's input.
    internal class FakeSwitcher : IMonitorSwitcher
    {
        public string EdidId { get; set; }
        public readonly List<byte> Switched = new List<byte>();
        public MonitorReading NextReading;
        public int Reads;

        public MonitorReading Read()
        {
            Reads++;
            return NextReading;
        }

        public void SwitchTo(InputDef input)
        {
            Switched.Add(input.Code);
        }
    }

    // End-to-end UI tests: drives the real tray app and Settings window in this process, clicking
    // every menu item and button, pressing real global hotkeys, and answering modal dialogs.
    // Uses Ctrl+Alt+F13..F16 so the user's running copy (Ctrl+Alt+1..3) is not disturbed.
    internal static class UiTests
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = AppInfo.Name;
        private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2;

        private static int _passed, _failed;
        private static readonly List<string> Failures = new List<string>();
        private static FakeSwitcher _fake;
        private static TrayApp _app;

        private static string IniPath { get { return Config.PathNextToExe; } }

        [STAThread]
        private static int Main()
        {
            Application.EnableVisualStyles();
            _originalRun = Registry.CurrentUser.OpenSubKey(RunKey).GetValue(RunValue);
            try
            {
                Run("Startup: detects the monitor and saves its inputs", Startup_DetectsAndSaves);
                Run("Tray menu: header shows the detected model", Menu_Header);
                Run("Tray menu: every visible input item switches to that input", Menu_InputItemsSwitch);
                Run("Tray menu: hidden inputs are left out", Menu_HiddenInputsLeftOut);
                Run("Hotkeys: pressing each shortcut switches", Hotkeys_Switch);
                Run("Hotkeys: ignored while a blocked program is focused", Hotkeys_BlockedProgram);
                Run("Tray menu: Reload settings file picks up hand edits", Menu_ReloadSettingsFile);
                Run("Tray menu: Settings... opens one window and releases hotkeys", Menu_SettingsOpens);
                Run("Tray icon: double-click opens Settings", Tray_DoubleClickOpensSettings);
                Run("Settings: Cancel button closes without saving", Settings_CancelButton);
                Run("Settings: Escape key closes without saving", Settings_EscapeKey);
                Run("Settings: window close (X) closes without saving", Settings_CloseBox);
                Run("Settings: shortcut box records, shows and clears keys", Settings_HotkeyBox);
                Run("Settings: duplicate shortcut is refused", Settings_DuplicateRefused);
                Run("Settings: shortcut without modifier on a typing key is refused", Settings_TypingKeyRefused);
                Run("Settings: Detect again updates inputs and keeps typed shortcuts", Settings_DetectAgain);
                Run("Settings: Detect again shows a read error", Settings_DetectError);
                Run("Settings: Add running program adds the chosen program once", Settings_AddRunningProgram);
                Run("Settings: Browse for .exe adds the picked program", Settings_Browse);
                Run("Settings: Remove deletes the selected programs", Settings_Remove);
                Run("Settings: Save writes the file and applies everything", Settings_Save);
                Run("Settings: Enter key saves", Settings_EnterSaves);
                Run("Tray menu: Start with Windows toggles the Run entry", Menu_StartWithWindows);
                Run("Tray menu: Edit settings file opens it in Notepad", Menu_EditSettingsFile);
                Run("Tray menu: Exit ends the app and removes the icon", Menu_Exit);
            }
            finally
            {
                if (_watchdog != null) _watchdog.Dispose();
                RestoreRunEntry();
                if (_app != null) _app.Dispose();
            }

            Console.WriteLine();
            Console.WriteLine("{0} passed, {1} failed", _passed, _failed);
            foreach (string f in Failures) Console.WriteLine("  FAILED: " + f);
            return _failed;
        }

        // ---------------------------------------------------------------- tests

        private static void Startup_DetectsAndSaves()
        {
            File.WriteAllText(IniPath,
                "[monitor]\r\nedid = GSM5C55\r\n" +
                "[hotkeys]\r\nhdmi1 = Ctrl+Alt+F13\r\nhdmi2 = Ctrl+Alt+F14\r\ndp1 = Ctrl+Alt+F15\r\n" +
                "[menu]\r\nhide = dp2\r\n[block]\r\nprograms =\r\n");
            _fake = new FakeSwitcher
            {
                NextReading = new MonitorReading { Model = "FAKE LG (TEST)", Inputs = new List<byte> { 0x11, 0x12, 0x0F, 0x10 } },
            };
            _app = new TrayApp(edid => { _fake.EdidId = edid; return _fake; });

            Check(WaitFor(() => File.ReadAllText(IniPath).Contains("inputs = 11 12 0F 10")), "ini gets the detected inputs");
            Check(File.ReadAllText(IniPath).Contains("model = FAKE LG (TEST)"), "ini gets the detected model");
            Check(File.ReadAllText(IniPath).Contains("hdmi1 = Ctrl+Alt+F13"), "existing shortcuts are kept");
            Check(_fake.Reads == 1, "monitor read once, got " + _fake.Reads);
        }

        private static void Menu_Header()
        {
            ToolStripItem header = Menu().Items[0];
            Check(header.Text == "FAKE LG (TEST)", "header text is '" + header.Text + "'");
            Check(!header.Enabled, "header is not clickable");
        }

        private static void Menu_InputItemsSwitch()
        {
            var expected = new Dictionary<string, byte> { { "HDMI 1", 0x11 }, { "HDMI 2", 0x12 }, { "DisplayPort 1", 0x0F } };
            foreach (var kv in expected)
            {
                _fake.Switched.Clear();
                ToolStripItem item = MenuItem(kv.Key);
                Check(item != null, kv.Key + " is in the menu");
                if (item == null) continue;
                item.PerformClick();
                Pump(50);
                Check(_fake.Switched.SequenceEqual(new[] { kv.Value }), kv.Key + " switched to " + Codes(_fake.Switched));
            }
            Check(((ToolStripMenuItem)MenuItem("HDMI 1")).ShortcutKeyDisplayString == "Ctrl+Alt+F13", "HDMI 1 shows its shortcut");
        }

        private static void Menu_HiddenInputsLeftOut()
        {
            Check(MenuItem("DisplayPort 2") == null, "DisplayPort 2 (hidden) is not in the menu");
        }

        private static void Hotkeys_Switch()
        {
            var keys = new[] { new { Vk = 0x7C, Code = (byte)0x11 }, new { Vk = 0x7D, Code = (byte)0x12 }, new { Vk = 0x7E, Code = (byte)0x0F } };
            foreach (var k in keys)
            {
                _fake.Switched.Clear();
                PressCtrlAlt(k.Vk);
                Check(WaitFor(() => _fake.Switched.Count > 0, 1000), "Ctrl+Alt+F" + (k.Vk - 0x6F) + " reached the app");
                Check(_fake.Switched.SequenceEqual(new[] { k.Code }), "Ctrl+Alt+F" + (k.Vk - 0x6F) + " switched to " + Codes(_fake.Switched));
            }
        }

        private static void Hotkeys_BlockedProgram()
        {
            string fg = Foreground.ExeName();
            Check(fg != null, "found the focused program (" + (fg ?? "none") + ")");
            if (fg == null) return;

            SetIniLine("programs", "programs = " + fg);
            MenuItem("Reload settings file").PerformClick();
            Pump(100);
            _fake.Switched.Clear();
            PressCtrlAlt(0x7C);
            Pump(400);
            Check(_fake.Switched.Count == 0, "no switch while " + fg + " is focused, got " + Codes(_fake.Switched));

            SetIniLine("programs", "programs =");
            MenuItem("Reload settings file").PerformClick();
            Pump(100);
            PressCtrlAlt(0x7C);
            Check(WaitFor(() => _fake.Switched.Count == 1, 1000), "switches again once unblocked");
        }

        private static void Menu_ReloadSettingsFile()
        {
            SetIniLine("hdmi2 =", "hdmi2 = Ctrl+Alt+F16");
            MenuItem("Reload settings file").PerformClick();
            Pump(100);
            Check(IsHotkeyTaken(0x7F), "new shortcut Ctrl+Alt+F16 is active");
            Check(!IsHotkeyTaken(0x7D), "old shortcut Ctrl+Alt+F14 is released");
            Check(((ToolStripMenuItem)MenuItem("HDMI 2")).ShortcutKeyDisplayString == "Ctrl+Alt+F16", "menu shows the new shortcut");
            SetIniLine("hdmi2 =", "hdmi2 = Ctrl+Alt+F14");
            MenuItem("Reload settings file").PerformClick();
            Pump(100);
        }

        private static void Menu_SettingsOpens()
        {
            MenuItem("Settings...").PerformClick();
            Check(WaitFor(() => OpenSettings().Count == 1), "Settings window opened");
            MenuItem("Settings...").PerformClick();
            Pump(100);
            Check(OpenSettings().Count == 1, "second click doesn't open another window");
            Check(!IsHotkeyTaken(0x7C), "hotkeys are released while Settings is open");
            CloseSettingsWith(f => f.Close());
            Check(IsHotkeyTaken(0x7C), "hotkeys come back after closing");
        }

        private static void Tray_DoubleClickOpensSettings()
        {
            NotifyIcon tray = Field<NotifyIcon>(_app, "_tray");
            typeof(NotifyIcon).GetMethod("OnMouseDoubleClick", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(tray, new object[] { new MouseEventArgs(MouseButtons.Left, 2, 0, 0, 0) });
            Check(WaitFor(() => OpenSettings().Count == 1), "Settings window opened");
            CloseSettingsWith(f => f.Close());
        }

        private static void Settings_CancelButton()
        {
            CancelLike("Cancel button", f => FindButton(f, "Cancel").PerformClick());
        }

        private static void Settings_EscapeKey()
        {
            CancelLike("Escape", f => InvokeProcessDialogKey(f, Keys.Escape));
        }

        private static void Settings_CloseBox()
        {
            CancelLike("X", f => f.Close());
        }

        private static void Settings_HotkeyBox()
        {
            SettingsForm f = OpenSettingsForm();
            HotkeyBox box = RowHotkey(f, "hdmi1");
            Check(box.Text == "Ctrl+Alt+F13", "shows the saved shortcut, got '" + box.Text + "'");

            PressInBox(box, Keys.ControlKey | Keys.Control);
            Check(box.Text == "Ctrl+", "shows held modifier, got '" + box.Text + "'");
            PressInBox(box, Keys.Menu | Keys.Control | Keys.Alt);
            Check(box.Text == "Ctrl+Alt+", "shows held modifiers, got '" + box.Text + "'");
            PressInBox(box, Keys.F20 | Keys.Control | Keys.Alt);
            Check(box.Text == "Ctrl+Alt+F20" && box.Hotkey == "Ctrl+Alt+F20", "records Ctrl+Alt+F20, got '" + box.Text + "'");
            PressInBox(box, Keys.D7 | Keys.Control | Keys.Shift);
            Check(box.Hotkey == "Ctrl+Shift+7", "digits shown without the D prefix, got '" + box.Hotkey + "'");
            PressInBox(box, Keys.Back);
            Check(box.Text == "(none)" && box.Hotkey == "", "Backspace clears it, got '" + box.Text + "'");
            bool tabHandled = PressInBox(box, Keys.Tab);
            Check(!tabHandled, "Tab is left for moving focus");
            CloseSettingsWith(x => x.Close());
        }

        private static void Settings_DuplicateRefused()
        {
            SettingsForm f = OpenSettingsForm();
            PressInBox(RowHotkey(f, "hdmi2"), Keys.F13 | Keys.Control | Keys.Alt);
            string message = AnswerMessageBox(() => FindButton(f, "Save").PerformClick());
            Check(message != null && message.Contains("already used"), "warns about the duplicate: " + (message ?? "no message box"));
            Check(OpenSettings().Count == 1, "window stays open");
            Check(!File.ReadAllText(IniPath).Contains("hdmi2 = Ctrl+Alt+F13"), "nothing saved");
            CloseSettingsWith(x => x.Close());
        }

        private static void Settings_TypingKeyRefused()
        {
            SettingsForm f = OpenSettingsForm();
            PressInBox(RowHotkey(f, "hdmi2"), Keys.A);
            string message = AnswerMessageBox(() => FindButton(f, "Save").PerformClick());
            Check(message != null && message.Contains("block normal typing"), "warns about plain A: " + (message ?? "no message box"));
            Check(OpenSettings().Count == 1, "window stays open");
            CloseSettingsWith(x => x.Close());
        }

        private static void Settings_DetectAgain()
        {
            SettingsForm f = OpenSettingsForm();
            PressInBox(RowHotkey(f, "hdmi1"), Keys.F21 | Keys.Control | Keys.Alt);
            _fake.NextReading = new MonitorReading { Model = "FAKE LG (TEST2)", Inputs = new List<byte> { 0x11, 0x12, 0x0F, 0x10, 0x1B } };
            FindButton(f, "Detect again").PerformClick();
            Pump(100);
            Check(Rows(f).Count == 5, "5 inputs after detection, got " + Rows(f).Count);
            Check(Field<Label>(f, "_monitorLabel").Text.Contains("FAKE LG (TEST2)"), "shows the new model");
            Check(RowHotkey(f, "hdmi1").Hotkey == "Ctrl+Alt+F21", "typed shortcut kept, got '" + RowHotkey(f, "hdmi1").Hotkey + "'");
            object usbc = Rows(f).First(r => Field<InputDef>(r, "Input").Key == "usbc");
            Check(!Field<CheckBox>(usbc, "Show").Enabled && !Field<HotkeyBox>(usbc, "Hotkey").Enabled, "USB-C (no switch code) can't be enabled");
            Check(File.ReadAllText(IniPath).Contains("inputs = 11 12 0F 10 1B"), "new inputs saved");
            CloseSettingsWith(x => x.Close());

            // Restore the original detection for later tests.
            _fake.NextReading = new MonitorReading { Model = "FAKE LG (TEST)", Inputs = new List<byte> { 0x11, 0x12, 0x0F, 0x10 } };
            f = OpenSettingsForm();
            FindButton(f, "Detect again").PerformClick();
            Pump(100);
            CloseSettingsWith(x => x.Close());
        }

        private static void Settings_DetectError()
        {
            SettingsForm f = OpenSettingsForm();
            int rows = Rows(f).Count;
            _fake.NextReading = new MonitorReading { Error = "no DDC reply (test)" };
            FindButton(f, "Detect again").PerformClick();
            Pump(100);
            Check(Field<Label>(f, "_monitorLabel").Text.Contains("no DDC reply (test)"), "error shown: " + Field<Label>(f, "_monitorLabel").Text.Replace("\n", " | "));
            Check(Rows(f).Count == rows, "inputs unchanged");
            Check(File.ReadAllText(IniPath).Contains("inputs = 11 12 0F 10"), "saved inputs untouched");
            _fake.NextReading = new MonitorReading { Model = "FAKE LG (TEST)", Inputs = new List<byte> { 0x11, 0x12, 0x0F, 0x10 } };
            CloseSettingsWith(x => x.Close());
        }

        private static void Settings_AddRunningProgram()
        {
            // Character Map: a classic Win32 app that runs as itself (Notepad on Windows 11 hands
            // off to the Store app and its launcher exits).
            var charmap = Process.Start(Path.Combine(Environment.SystemDirectory, "charmap.exe"));
            try
            {
                charmap.WaitForInputIdle(5000);
                WaitFor(() => { charmap.Refresh(); return charmap.MainWindowHandle != IntPtr.Zero; });
                SettingsForm f = OpenSettingsForm();
                ListBox programs = Field<ListBox>(f, "_programs");
                programs.Items.Clear();
                Button add = FindButton(f, "Add running program ▾");

                for (int round = 1; round <= 2; round++)
                {
                    // A drop-down closes as soon as its window loses focus, so click it the way a
                    // user does: with the Settings window in front.
                    BringToFront(f);
                    add.PerformClick();
                    ContextMenuStrip menu = null;
                    Check(WaitFor(() => (menu = OpenContextMenu()) != null), "program menu opened (round " + round + ")");
                    if (menu == null) break;
                    ToolStripItem entry = menu.Items.Cast<ToolStripItem>().FirstOrDefault(i => i.Text.StartsWith("charmap.exe", StringComparison.OrdinalIgnoreCase));
                    Check(entry != null, "running Character Map is listed (" + menu.Items.Count + " entries)");
                    if (entry != null) entry.PerformClick();
                    menu.Close();
                    Pump(100);
                    Check(programs.Items.Count == 1 && (string)programs.Items[0] == "charmap.exe",
                        round == 1 ? "charmap.exe added" : "adding it twice keeps one entry");
                }
                CloseSettingsWith(x => x.Close());
            }
            finally
            {
                if (!charmap.HasExited) charmap.Kill();
                charmap.Dispose();
            }
        }

        private static void Settings_Browse()
        {
            SettingsForm f = OpenSettingsForm();
            ListBox programs = Field<ListBox>(f, "_programs");
            programs.Items.Clear();
            string picked = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "regedit.exe");
            bool answered = AnswerFileDialog("Choose a program", picked, () => FindButton(f, "Browse for .exe...").PerformClick());
            Check(answered, "file dialog appeared and was answered");
            Check(programs.Items.Count == 1 && (string)programs.Items[0] == "regedit.exe", "regedit.exe added, list: " + string.Join(",", programs.Items.Cast<string>()));
            CloseSettingsWith(x => x.Close());
        }

        private static void Settings_Remove()
        {
            SettingsForm f = OpenSettingsForm();
            ListBox programs = Field<ListBox>(f, "_programs");
            programs.Items.Clear();
            programs.Items.AddRange(new object[] { "a.exe", "b.exe", "c.exe" });
            programs.SelectedItems.Clear();
            programs.SetSelected(0, true);
            programs.SetSelected(2, true);
            FindButton(f, "Remove").PerformClick();
            Check(programs.Items.Count == 1 && (string)programs.Items[0] == "b.exe", "a.exe and c.exe removed, left: " + string.Join(",", programs.Items.Cast<string>()));
            programs.SelectedItems.Clear();
            FindButton(f, "Remove").PerformClick();
            Check(programs.Items.Count == 1, "Remove with nothing selected does nothing");
            CloseSettingsWith(x => x.Close());
        }

        private static void Settings_Save()
        {
            SettingsForm f = OpenSettingsForm();
            PressInBox(RowHotkey(f, "hdmi1"), Keys.F16 | Keys.Control | Keys.Alt);
            Field<CheckBox>(Row(f, "hdmi2"), "Show").Checked = false;
            PressInBox(RowHotkey(f, "dp1"), Keys.Back);
            ListBox programs = Field<ListBox>(f, "_programs");
            programs.Items.Clear();
            programs.Items.Add("Overwatch.exe");
            CloseSettingsWith(x => FindButton(x, "Save").PerformClick());

            string ini = File.ReadAllText(IniPath);
            Check(ini.Contains("hdmi1 = Ctrl+Alt+F16"), "new HDMI 1 shortcut saved");
            Check(!ini.Contains("dp1 ="), "cleared DP shortcut removed");
            Check(ini.Contains("hide = dp2 hdmi2"), "HDMI 2 hidden (hide line: " + Line(ini, "hide") + ")");
            Check(ini.Contains("programs = Overwatch.exe"), "blocked program saved");
            Check(MenuItem("HDMI 2") == null, "HDMI 2 gone from the menu");
            Check(IsHotkeyTaken(0x7F), "Ctrl+Alt+F16 active");
            Check(!IsHotkeyTaken(0x7C), "old Ctrl+Alt+F13 released");
            Check(!IsHotkeyTaken(0x7E), "cleared Ctrl+Alt+F15 released");
            _fake.Switched.Clear();
            PressCtrlAlt(0x7F);
            Check(WaitFor(() => _fake.Switched.Count > 0, 1000) && _fake.Switched[0] == 0x11, "Ctrl+Alt+F16 switches to HDMI 1");
        }

        private static void Settings_EnterSaves()
        {
            SettingsForm f = OpenSettingsForm();
            Field<CheckBox>(Row(f, "hdmi2"), "Show").Checked = true;
            RowHotkey(f, "hdmi2").Focus();
            CloseSettingsWith(x => InvokeProcessDialogKey(x, Keys.Enter));
            Check(MenuItem("HDMI 2") != null, "HDMI 2 back in the menu after Enter");
        }

        private static void Menu_StartWithWindows()
        {
            ToolStripMenuItem item = (ToolStripMenuItem)MenuItem("Start with Windows");
            bool before = item.Checked;
            item.PerformClick();
            object value = Registry.CurrentUser.OpenSubKey(RunKey).GetValue(RunValue);
            Check(item.Checked == !before, "tick toggled");
            Check(item.Checked ? value as string == "\"" + Application.ExecutablePath + "\"" : value == null,
                "Run entry " + (item.Checked ? "points at the exe" : "removed") + " (value: " + (value ?? "none") + ")");
            item.PerformClick();
            value = Registry.CurrentUser.OpenSubKey(RunKey).GetValue(RunValue);
            Check(item.Checked == before, "tick toggled back");
            Check(item.Checked ? value != null : value == null, "Run entry toggled back");
        }

        private static void Menu_EditSettingsFile()
        {
            DateTime start = DateTime.Now.AddSeconds(-1);
            MenuItem("Edit settings file").PerformClick();
            Process opened = null;
            Check(WaitFor(() => (opened = FindNotepadWith(IniPath, start)) != null, 5000), "Notepad opened with " + IniPath);
            if (opened != null) { opened.Kill(); opened.Dispose(); }
        }

        private static void Menu_Exit()
        {
            bool exited = false;
            _app.ThreadExit += delegate { exited = true; };
            NotifyIcon tray = Field<NotifyIcon>(_app, "_tray");
            MenuItem("Exit").PerformClick();
            Pump(100);
            Check(exited, "app asked the message loop to end");
            Check(!tray.Visible, "tray icon removed");
            Check(!IsHotkeyTaken(0x7F), "hotkeys released");
        }

        // ---------------------------------------------------------------- helpers: settings

        private static void CancelLike(string how, Action<SettingsForm> close)
        {
            string before = File.ReadAllText(IniPath);
            SettingsForm f = OpenSettingsForm();
            PressInBox(RowHotkey(f, "hdmi1"), Keys.F22 | Keys.Control | Keys.Alt);
            Field<CheckBox>(Row(f, "hdmi2"), "Show").Checked = false;
            close(f);
            Pump(200);
            Check(OpenSettings().Count == 0, how + " closes the window");
            Check(File.ReadAllText(IniPath) == before, how + " doesn't change the settings file");
            Check(IsHotkeyTaken(0x7C), how + ": hotkeys are active again");

            // If it didn't close, clean up so the following tests start from a closed window.
            if (OpenSettings().Count > 0)
            {
                foreach (SettingsForm stuck in OpenSettings()) stuck.Dispose();
                ForceResetSettings();
            }
        }

        private static void ForceResetSettings()
        {
            typeof(TrayApp).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(_app, null);
            MenuItem("Reload settings file").PerformClick();
            Pump(100);
        }

        private static SettingsForm OpenSettingsForm()
        {
            MenuItem("Settings...").PerformClick();
            WaitFor(() => OpenSettings().Count == 1);
            return OpenSettings().First();
        }

        private static void CloseSettingsWith(Action<SettingsForm> close)
        {
            List<SettingsForm> open = OpenSettings();
            if (open.Count == 0) return;
            close(open[0]);
            WaitFor(() => OpenSettings().Count == 0);
            Pump(100);
            if (OpenSettings().Count > 0)
            {
                foreach (SettingsForm f in OpenSettings()) f.Dispose();
                ForceResetSettings();
            }
        }

        private static List<SettingsForm> OpenSettings()
        {
            return Application.OpenForms.OfType<SettingsForm>().Where(f => !f.IsDisposed && f.Visible).ToList();
        }

        private static List<object> Rows(SettingsForm f)
        {
            return ((IEnumerable)Field<object>(f, "_rows")).Cast<object>().ToList();
        }

        private static object Row(SettingsForm f, string key)
        {
            return Rows(f).First(r => Field<InputDef>(r, "Input").Key == key);
        }

        private static HotkeyBox RowHotkey(SettingsForm f, string key)
        {
            return Field<HotkeyBox>(Row(f, key), "Hotkey");
        }

        private static bool PressInBox(HotkeyBox box, Keys keyData)
        {
            var msg = Message.Create(box.Handle, 0x100, IntPtr.Zero, IntPtr.Zero);
            object[] args = { msg, keyData };
            return (bool)typeof(HotkeyBox).GetMethod("ProcessCmdKey", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(box, args);
        }

        private static void InvokeProcessDialogKey(Form f, Keys key)
        {
            typeof(Form).GetMethod("ProcessDialogKey", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(f, new object[] { key });
        }

        private static Button FindButton(Control root, string text)
        {
            foreach (Control c in root.Controls)
            {
                var b = c as Button;
                if (b != null && b.Text == text) return b;
                Button inner = FindButton(c, text);
                if (inner != null) return inner;
            }
            return null;
        }

        // ---------------------------------------------------------------- helpers: tray / hotkeys

        private static ContextMenuStrip Menu()
        {
            return Field<NotifyIcon>(_app, "_tray").ContextMenuStrip;
        }

        private static ToolStripItem MenuItem(string text)
        {
            return Menu().Items.Cast<ToolStripItem>().FirstOrDefault(i => i.Text == text);
        }

        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

        // True if some window already owns Ctrl+Alt+<vk>.
        private static bool IsHotkeyTaken(int vk)
        {
            Pump(20);
            if (!RegisterHotKey(IntPtr.Zero, 0xBEEF, MOD_CONTROL | MOD_ALT, (uint)vk)) return true;
            UnregisterHotKey(IntPtr.Zero, 0xBEEF);
            return false;
        }

        private static void PressCtrlAlt(int vk)
        {
            const uint KEYUP = 0x2;
            keybd_event(0x11, 0, 0, UIntPtr.Zero);
            keybd_event(0x12, 0, 0, UIntPtr.Zero);
            keybd_event((byte)vk, 0, 0, UIntPtr.Zero);
            keybd_event((byte)vk, 0, KEYUP, UIntPtr.Zero);
            keybd_event(0x12, 0, KEYUP, UIntPtr.Zero);
            keybd_event(0x11, 0, KEYUP, UIntPtr.Zero);
            Pump(150);
        }

        // ---------------------------------------------------------------- helpers: modal dialogs

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr dlg, int id);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, string l);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        private delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr l);
        [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, EnumProc proc, IntPtr l);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

        private const uint WM_COMMAND = 0x0111, WM_SETTEXT = 0x000C, WM_CLOSE = 0x0010;

        private static IntPtr WaitForOwnDialog(string title, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                IntPtr h = FindWindow("#32770", title);
                uint pid;
                if (h != IntPtr.Zero && GetWindowThreadProcessId(h, out pid) != 0 && pid == (uint)Process.GetCurrentProcess().Id) return h;
                Thread.Sleep(50);
            }
            return IntPtr.Zero;
        }

        // Runs an action that shows a message box titled AppInfo.Name, returns its text and presses OK.
        private static string AnswerMessageBox(Action trigger)
        {
            string text = null;
            Task answer = Task.Run(() =>
            {
                IntPtr dlg = WaitForOwnDialog(AppInfo.Name, 5000);
                if (dlg == IntPtr.Zero) return;
                var sb = new StringBuilder(1024);
                GetWindowText(GetDlgItem(dlg, 0xFFFF), sb, sb.Capacity);
                text = sb.ToString();
                PostMessage(dlg, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            });
            trigger();
            answer.Wait(6000);
            Pump(100);
            return text;
        }

        // Runs an action that shows an open-file dialog, types the path and presses Open.
        private static bool AnswerFileDialog(string title, string path, Action trigger)
        {
            bool done = false;
            Task answer = Task.Run(() =>
            {
                IntPtr dlg = WaitForOwnDialog(title, 8000);
                if (dlg == IntPtr.Zero) return;
                Thread.Sleep(500); // let the dialog finish building its controls
                IntPtr edit = IntPtr.Zero;
                EnumChildWindows(dlg, (h, l) =>
                {
                    var cls = new StringBuilder(64);
                    GetClassName(h, cls, cls.Capacity);
                    if (cls.ToString() == "Edit" && edit == IntPtr.Zero)
                    {
                        // The file-name box is an Edit inside the combo with control id 0x47C.
                        IntPtr parent = GetParentWindow(h);
                        if (GetDlgCtrlID(parent) == 0x47C || GetDlgCtrlID(GetParentWindow(parent)) == 0x47C) edit = h;
                    }
                    return true;
                }, IntPtr.Zero);
                if (edit == IntPtr.Zero) { PostMessage(dlg, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); return; }
                SendMessage(edit, WM_SETTEXT, IntPtr.Zero, path);
                SendMessage(dlg, WM_COMMAND, (IntPtr)1, IntPtr.Zero); // IDOK = Open
                done = true;
            });
            trigger();
            answer.Wait(10000);
            Pump(100);
            return done;
        }

        [DllImport("user32.dll", EntryPoint = "GetParent")] private static extern IntPtr GetParentWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);

        // Windows refuses SetForegroundWindow from a background process; sharing input state with
        // the current foreground thread lifts that, without sending keys anywhere.
        private static void BringToFront(Form f)
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == f.Handle) return;
            uint pid;
            uint fgThread = GetWindowThreadProcessId(fg, out pid);
            uint me = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
            try
            {
                SetForegroundWindow(f.Handle);
                f.Activate();
            }
            finally
            {
                if (attached) AttachThreadInput(me, fgThread, false);
            }
            WaitFor(() => GetForegroundWindow() == f.Handle, 1000);
        }

        private static ContextMenuStrip OpenContextMenu()
        {
            ContextMenuStrip found = null;
            EnumThreadWindows(GetCurrentThreadId(), (h, l) =>
            {
                var menu = Control.FromHandle(h) as ContextMenuStrip;
                if (menu != null && menu.Visible && menu != Menu()) found = menu;
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static Process FindNotepadWith(string path, DateTime after)
        {
            using (var search = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'notepad.exe'"))
            {
                foreach (ManagementObject p in search.Get())
                {
                    string cmd = p["CommandLine"] as string;
                    if (cmd == null || cmd.IndexOf(path, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    Process proc = Process.GetProcessById(Convert.ToInt32(p["ProcessId"]));
                    if (proc.StartTime >= after) return proc;
                }
            }
            return null;
        }

        // ---------------------------------------------------------------- helpers: general

        private static System.Threading.Timer _watchdog;
        private static object _originalRun;

        // Ends the run if a test is stuck (e.g. an unanswered modal dialog), restoring the Run entry.
        private static void ArmWatchdog(string name)
        {
            if (_watchdog != null) _watchdog.Dispose();
            _watchdog = new System.Threading.Timer(delegate
            {
                Console.WriteLine("TIMEOUT  " + name + " (stuck for 60 s; aborting the run)");
                RestoreRunEntry();
                Environment.Exit(99);
            }, null, 60000, Timeout.Infinite);
        }

        private static void RestoreRunEntry()
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (_originalRun != null) key.SetValue(RunValue, _originalRun);
                else key.DeleteValue(RunValue, false);
            }
        }

        private static void Run(string name, Action test)
        {
            int failedBefore = _failed;
            _currentTest = name;
            ArmWatchdog(name);
            try { test(); }
            catch (Exception e)
            {
                Exception inner = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                Fail("threw " + inner.GetType().Name + ": " + inner.Message);
            }
            Console.WriteLine("{0}  {1}", _failed == failedBefore ? "PASS" : "FAIL", name);
            if (_failed == failedBefore) _passed++;
        }

        private static string _currentTest;

        private static void Check(bool ok, string what)
        {
            if (ok) return;
            Fail(what);
        }

        private static void Fail(string what)
        {
            _failed++;
            Failures.Add(_currentTest + ": " + what);
            Console.WriteLine("      x " + what);
        }

        private static bool WaitFor(Func<bool> condition, int timeoutMs = 3000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                Application.DoEvents();
                if (condition()) return true;
                Thread.Sleep(10);
            }
            Application.DoEvents();
            return condition();
        }

        private static void Pump(int ms)
        {
            var sw = Stopwatch.StartNew();
            do { Application.DoEvents(); Thread.Sleep(5); } while (sw.ElapsedMilliseconds < ms);
        }

        private static T Field<T>(object obj, string name)
        {
            FieldInfo f = obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            return (T)f.GetValue(obj);
        }

        private static void SetIniLine(string startsWith, string replacement)
        {
            string[] lines = File.ReadAllLines(IniPath);
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].TrimStart().StartsWith(startsWith, StringComparison.OrdinalIgnoreCase)) lines[i] = replacement;
            if (!lines.Any(l => l == replacement))
            {
                // Key not present yet: add it to the matching section.
                var list = lines.ToList();
                string section = startsWith.StartsWith("programs") ? "[block]" : "[hotkeys]";
                list.Insert(list.IndexOf(section) + 1, replacement);
                lines = list.ToArray();
            }
            File.WriteAllLines(IniPath, lines);
        }

        private static string Line(string text, string key)
        {
            return text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(key)) ?? "(missing)";
        }

        private static string Codes(IEnumerable<byte> codes)
        {
            return "[" + string.Join(" ", codes.Select(c => c.ToString("X2"))) + "]";
        }
    }
}
