using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LgInputSwitcher
{
    internal class TrayApp : ApplicationContext
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = AppInfo.Name;

        private readonly NotifyIcon _tray;
        private readonly HotkeyWindow _hotkeys;
        private readonly SynchronizationContext _ui;
        private Config _config;
        private List<InputDef> _inputs = new List<InputDef>();
        private readonly Func<string, IMonitorSwitcher> _createSwitcher;
        private IMonitorSwitcher _switcher;
        private SettingsForm _settings;
        private bool _configLoadFailed; // never auto-save over a file we couldn't read

        public TrayApp(Func<string, IMonitorSwitcher> createSwitcher = null)
        {
            _createSwitcher = createSwitcher ?? (edid => new LgSwitcher(edid));
            _ui = new WindowsFormsSynchronizationContext();
            _hotkeys = new HotkeyWindow();
            _hotkeys.HotkeyPressed += OnHotkey;
            _tray = new NotifyIcon { Icon = CreateIcon(), Text = AppInfo.Name, Visible = true };
            _tray.MouseDoubleClick += delegate { ShowSettings(); };
            LoadSettings(null);
            // The inputs are cached in the ini; Settings > Detect again refreshes them.
            if (_config.InputCodesDetected.Count == 0) DetectInBackground();
        }

        private IMonitorSwitcher Switcher
        {
            get
            {
                // Kept for the app's lifetime so it remembers the monitor's port.
                if (_switcher == null || !_switcher.EdidId.Equals(_config.EdidId, StringComparison.OrdinalIgnoreCase))
                    _switcher = _createSwitcher(_config.EdidId);
                return _switcher;
            }
        }

        private void LoadSettings(string announce)
        {
            var failed = new List<string>();
            try
            {
                _config = Config.Load();
                _configLoadFailed = false;
            }
            catch (Exception e)
            {
                _config = new Config();
                _configLoadFailed = true;
                failed.Add("lg-input-switcher.ini: " + e.Message);
            }
            _inputs = _config.Inputs;

            BuildMenu();
            failed.AddRange(RegisterHotkeys());

            if (failed.Count > 0)
                Notify("Some shortcuts were not registered:\n" + string.Join("\n", failed), ToolTipIcon.Warning);
            else if (announce != null)
                Notify(announce, ToolTipIcon.Info);
        }

        private List<string> RegisterHotkeys()
        {
            _hotkeys.UnregisterAll();
            var failed = new List<string>();
            if (_settings != null) return failed; // released while Settings records new shortcuts

            for (int i = 0; i < _inputs.Count; i++)
            {
                InputDef input = _inputs[i];
                if (input.Hidden || input.LgValue == null || string.IsNullOrEmpty(input.Hotkey)) continue;
                uint mods;
                Keys key;
                if (!Config.TryParseHotkey(input.Hotkey, out mods, out key))
                    failed.Add(input.Hotkey + " (not understood)");
                else if (!_hotkeys.Register(i, mods, key))
                    failed.Add(input.Hotkey + " (in use by another app)");
            }
            return failed;
        }

        // Reads the monitor's model and inputs at startup without blocking the tray.
        private void DetectInBackground()
        {
            IMonitorSwitcher switcher;
            try { switcher = Switcher; } // created here, on the UI thread
            catch (Exception e)
            {
                Notify("NVIDIA display access failed:\n" + e.Message, ToolTipIcon.Error);
                return;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                MonitorReading reading;
                try { reading = switcher.Read(); }
                catch (Exception e) { reading = new MonitorReading { Error = e.Message }; }
                _ui.Post(delegate { ApplyDetection(reading, false); }, null);
            });
        }

        private MonitorReading DetectNow()
        {
            MonitorReading reading;
            try { reading = Switcher.Read(); }
            catch (Exception e) { reading = new MonitorReading { Error = e.Message }; }
            ApplyDetection(reading, true);
            return reading;
        }

        // Stores newly detected inputs/model in the settings file if they changed.
        private void ApplyDetection(MonitorReading reading, bool quiet)
        {
            if (reading.Inputs == null)
            {
                if (!quiet && _config.InputCodesDetected.Count == 0)
                    Notify("Couldn't read the monitor's inputs:\n" + reading.Error, ToolTipIcon.Warning);
                return;
            }
            if (reading.Model == _config.Model && reading.Inputs.SequenceEqual(_config.InputCodesDetected)) return;

            _config.Model = reading.Model;
            _config.InputCodesDetected.Clear();
            _config.InputCodesDetected.AddRange(reading.Inputs);
            if (_configLoadFailed)
            {
                // Keep the detection in memory only; saving would wipe the user's unreadable file.
                _inputs = _config.Inputs;
                BuildMenu();
                return;
            }
            try { _config.Save(); }
            catch (Exception e) { Notify("Could not save settings:\n" + e.Message, ToolTipIcon.Error); }
            LoadSettings(null);
        }

        private void BuildMenu()
        {
            var menu = new ContextMenuStrip();
            var header = new ToolStripMenuItem(string.IsNullOrEmpty(_config.Model) ? "Monitor not detected yet" : _config.Model) { Enabled = false };
            menu.Items.Add(header);

            foreach (InputDef input in _inputs)
            {
                if (input.Hidden || input.LgValue == null) continue;
                InputDef captured = input;
                var item = new ToolStripMenuItem(input.Label, null, delegate { Switch(captured); });
                if (!string.IsNullOrEmpty(input.Hotkey)) item.ShortcutKeyDisplayString = input.Hotkey;
                menu.Items.Add(item);
            }
            menu.Items.Add(new ToolStripSeparator());

            var startup = new ToolStripMenuItem("Start with Windows") { Checked = IsStartupEnabled() };
            startup.Click += delegate
            {
                SetStartup(!startup.Checked);
                startup.Checked = IsStartupEnabled();
            };
            var settings = new ToolStripMenuItem("Settings...", null, delegate { ShowSettings(); });
            settings.Font = new Font(settings.Font, FontStyle.Bold);
            menu.Items.Add(settings);
            menu.Items.Add(startup);
            menu.Items.Add("Edit settings file", null, delegate { Process.Start("notepad.exe", "\"" + Config.PathNextToExe + "\""); });
            menu.Items.Add("Reload settings file", null, delegate { LoadSettings("Settings reloaded"); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { ExitThread(); });

            ContextMenuStrip old = _tray.ContextMenuStrip;
            _tray.ContextMenuStrip = menu;
            if (old != null) old.Dispose();
        }

        private void OnHotkey(int id)
        {
            if (id < 0 || id >= _inputs.Count) return;
            // Never switch away from a blocked program (e.g. a game) while it's in front.
            if (_config.IsBlocked(Foreground.ExeName())) return;
            Switch(_inputs[id]);
        }

        private void Switch(InputDef input)
        {
            try
            {
                Switcher.SwitchTo(input);
                _tray.Text = Truncate(AppInfo.Name + " - " + input.Label, 63);
            }
            catch (Exception e)
            {
                Notify("Could not switch to " + input.Label + ":\n" + e.Message, ToolTipIcon.Error);
            }
        }

        private void ShowSettings()
        {
            if (_settings != null)
            {
                _settings.Activate();
                return;
            }

            _settings = new SettingsForm(_config, DetectNow);
            RegisterHotkeys(); // releases them so they can be recorded in the form
            _settings.FormClosed += delegate
            {
                Config result = _settings.Result;
                _settings.Dispose();
                _settings = null;
                if (result != null)
                {
                    try { result.Save(); }
                    catch (Exception e) { Notify("Could not save settings:\n" + e.Message, ToolTipIcon.Error); }
                    LoadSettings("Settings saved");
                }
                else
                {
                    RegisterHotkeys();
                }
            };
            _settings.Show();
            _settings.Activate();
        }

        private void Notify(string text, ToolTipIcon icon)
        {
            _tray.ShowBalloonTip(4000, AppInfo.Name, text, icon);
        }

        private static string Truncate(string s, int max)
        {
            return s.Length <= max ? s : s.Substring(0, max);
        }

        private static bool IsStartupEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                return key != null && key.GetValue(RunValue) != null;
        }

        private static void SetStartup(bool enable)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enable) key.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
                else key.DeleteValue(RunValue, false);
            }
        }

        private static Icon CreateIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var frame = new Pen(Color.White, 3))
                using (var screen = new SolidBrush(Color.FromArgb(200, 30, 60)))
                using (var stand = new SolidBrush(Color.White))
                {
                    g.FillRectangle(screen, 3, 4, 26, 17);
                    g.DrawRectangle(frame, 3, 4, 26, 17);
                    g.FillRectangle(stand, 14, 22, 4, 4);
                    g.FillRectangle(stand, 9, 26, 14, 3);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        // Exit: remove the tray icon and shortcuts now. Application.Run returns without disposing
        // the context, so otherwise a ghost icon stays in the tray until the mouse passes over it.
        protected override void ExitThreadCore()
        {
            if (_settings != null) _settings.Dispose();
            _hotkeys.UnregisterAll();
            _tray.Visible = false;
            base.ExitThreadCore();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hotkeys.UnregisterAll();
                _hotkeys.DestroyHandle();
                _tray.Visible = false;
                _tray.Dispose();
            }
            base.Dispose(disposing);
        }

        private class HotkeyWindow : NativeWindow
        {
            private const int WM_HOTKEY = 0x0312;
            private const uint MOD_NOREPEAT = 0x4000;
            private readonly List<int> _ids = new List<int>();

            public event Action<int> HotkeyPressed;

            [DllImport("user32.dll", SetLastError = true)]
            private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

            [DllImport("user32.dll", SetLastError = true)]
            private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

            public HotkeyWindow()
            {
                CreateHandle(new CreateParams());
            }

            public bool Register(int id, uint modifiers, Keys key)
            {
                if (!RegisterHotKey(Handle, id, modifiers | MOD_NOREPEAT, (uint)key)) return false;
                _ids.Add(id);
                return true;
            }

            public void UnregisterAll()
            {
                foreach (int id in _ids) UnregisterHotKey(Handle, id);
                _ids.Clear();
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_HOTKEY && HotkeyPressed != null)
                    HotkeyPressed(m.WParam.ToInt32());
                base.WndProc(ref m);
            }
        }
    }
}
