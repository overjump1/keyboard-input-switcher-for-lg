using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LgInputSwitcher
{
    internal class SettingsForm : Form
    {
        private class Row
        {
            public InputDef Input;
            public Label Name;
            public CheckBox Show;
            public HotkeyBox Hotkey;
        }

        private readonly Config _original;
        private readonly Func<MonitorReading> _detect;
        private readonly List<Row> _rows = new List<Row>();
        private readonly TableLayoutPanel _grid;
        private readonly Label _monitorLabel;
        private readonly ListBox _programs;
        private string _model;
        private List<byte> _inputs;

        public Config Result { get; private set; }

        public SettingsForm(Config config, Func<MonitorReading> detect)
        {
            _original = config;
            _detect = detect;
            _model = config.Model;
            _inputs = new List<byte>(config.InputCodesDetected);

            Text = AppInfo.Name + " - Settings";
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(10);

            var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
            Controls.Add(root);

            // --- Monitor ------------------------------------------------------------
            var monitorBox = new GroupBox { Text = "Monitor", AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(8) };
            var monitorLayout = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
            monitorLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 420));
            monitorLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _monitorLabel = new Label { AutoSize = true, MaximumSize = new Size(410, 0), Anchor = AnchorStyles.Left };
            var detectButton = new Button { Text = "Detect again", AutoSize = true, Anchor = AnchorStyles.Right };
            detectButton.Click += delegate { Redetect(); };
            monitorLayout.Controls.Add(_monitorLabel);
            monitorLayout.Controls.Add(detectButton);
            monitorBox.Controls.Add(monitorLayout);
            root.Controls.Add(monitorBox);

            // --- Inputs -------------------------------------------------------------
            var inputsBox = new GroupBox { Text = "Inputs and shortcuts", AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(8) };
            var inputsLayout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
            inputsBox.Controls.Add(inputsLayout);
            inputsLayout.Controls.Add(Hint("Inputs are the ones your monitor reports. Click a shortcut box and press the keys; Backspace clears it."));
            _grid = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
            foreach (int w in new[] { 250, 110, 180 })
                _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, w));
            inputsLayout.Controls.Add(_grid);
            root.Controls.Add(inputsBox);

            // --- Block programs -----------------------------------------------------
            var blockBox = new GroupBox { Text = "Don't switch while one of these programs' window is selected", AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(8) };
            var blockLayout = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
            blockBox.Controls.Add(blockLayout);

            var blockHint = Hint("Shortcuts do nothing while you're in one of these windows, so a stray key press can't switch you out of a game. Running in the background or minimized doesn't count.");
            blockHint.MaximumSize = new Size(560, 0);
            blockLayout.Controls.Add(blockHint);
            blockLayout.SetColumnSpan(blockHint, 2);

            _programs = new ListBox { Width = 370, Height = 110, IntegralHeight = false, SelectionMode = SelectionMode.MultiExtended };
            foreach (string p in config.BlockPrograms) _programs.Items.Add(p);
            blockLayout.Controls.Add(_programs);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
            var addRunning = new Button { Text = "Add running program ▾", AutoSize = true, MinimumSize = new Size(170, 0) };
            addRunning.Click += delegate { ShowRunningPrograms(addRunning); };
            var browse = new Button { Text = "Browse for .exe...", AutoSize = true, MinimumSize = new Size(170, 0) };
            browse.Click += delegate { BrowseProgram(); };
            var remove = new Button { Text = "Remove", AutoSize = true, MinimumSize = new Size(170, 0) };
            remove.Click += delegate
            {
                foreach (object item in _programs.SelectedItems.Cast<object>().ToList()) _programs.Items.Remove(item);
            };
            buttons.Controls.AddRange(new Control[] { addRunning, browse, remove });
            blockLayout.Controls.Add(buttons);
            root.Controls.Add(blockBox);

            // --- Save / Cancel ------------------------------------------------------
            var bottom = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
            // The form is shown modeless (Show, not ShowDialog), where a button's DialogResult
            // doesn't close the window, so Cancel (and Escape, via CancelButton) close explicitly.
            var cancel = new Button { Text = "Cancel", AutoSize = true };
            cancel.Click += delegate { Close(); };
            var save = new Button { Text = "Save", AutoSize = true };
            save.Click += delegate { OnSave(); };
            bottom.Controls.Add(cancel);
            bottom.Controls.Add(save);
            root.Controls.Add(bottom);
            AcceptButton = save;
            CancelButton = cancel;

            ApplyReading(null);
        }

        private static Label Hint(string text)
        {
            return new Label { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 0) };
        }

        private void Redetect()
        {
            Cursor = Cursors.WaitCursor;
            try { ApplyReading(_detect()); }
            finally { Cursor = Cursors.Default; }
        }

        // Shows the monitor (from a fresh reading, or the saved settings when reading is null)
        // and rebuilds the input rows, keeping any shortcuts already entered in this window.
        private void ApplyReading(MonitorReading reading)
        {
            if (reading != null && reading.Inputs != null)
            {
                _model = reading.Model;
                _inputs = reading.Inputs;
            }

            string text = string.IsNullOrEmpty(_model) ? "Not detected yet" : _model;
            if (reading != null && reading.Error != null) text += "\nCouldn't read the monitor: " + reading.Error;
            _monitorLabel.Text = text;

            var hotkeys = new Dictionary<string, string>(_original.Hotkeys, StringComparer.OrdinalIgnoreCase);
            var hidden = new HashSet<string>(_original.Hidden, StringComparer.OrdinalIgnoreCase);
            foreach (Row r in _rows)
            {
                hotkeys[r.Input.Key] = r.Hotkey.Hotkey;
                if (r.Show.Checked) hidden.Remove(r.Input.Key); else hidden.Add(r.Input.Key);
            }

            _rows.Clear();
            foreach (byte code in _inputs)
            {
                InputDef input = InputCodes.Describe(code);
                string hk;
                var row = new Row
                {
                    Input = input,
                    Name = new Label { Text = input.Label, AutoSize = true, Anchor = AnchorStyles.Left },
                    Show = new CheckBox { Text = "Show", Checked = !hidden.Contains(input.Key), AutoSize = true, Anchor = AnchorStyles.Left },
                    Hotkey = new HotkeyBox { Dock = DockStyle.Fill },
                };
                row.Hotkey.Hotkey = hotkeys.TryGetValue(input.Key, out hk) ? hk : "";
                if (input.LgValue == null)
                {
                    row.Name.Text += "  (switch code unknown)";
                    row.Show.Checked = row.Show.Enabled = false;
                    row.Hotkey.Enabled = false;
                }
                _rows.Add(row);
            }
            RebuildGrid();
        }

        private void RebuildGrid()
        {
            _grid.SuspendLayout();
            _grid.Controls.Clear();
            _grid.RowStyles.Clear();
            _grid.RowCount = _rows.Count + 1;

            string[] headers = { "Input", "In tray menu", "Shortcut" };
            for (int c = 0; c < headers.Length; c++)
                _grid.Controls.Add(new Label { Text = headers[c], AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, c, 0);

            for (int r = 0; r < _rows.Count; r++)
            {
                _grid.Controls.Add(_rows[r].Name, 0, r + 1);
                _grid.Controls.Add(_rows[r].Show, 1, r + 1);
                _grid.Controls.Add(_rows[r].Hotkey, 2, r + 1);
            }
            if (_rows.Count == 0)
            {
                var none = Hint("No inputs detected. Click Detect again.");
                _grid.RowCount = 2;
                _grid.Controls.Add(none, 0, 1);
                _grid.SetColumnSpan(none, 3);
            }
            _grid.ResumeLayout();
        }

        private void ShowRunningPrograms(Control anchor)
        {
            var names = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int self = Process.GetCurrentProcess().Id;
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id != self && p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrEmpty(p.MainWindowTitle))
                        names[p.ProcessName + ".exe"] = p.MainWindowTitle;
                }
                catch { }
                finally { p.Dispose(); }
            }

            var menu = new ContextMenuStrip();
            foreach (KeyValuePair<string, string> kv in names)
            {
                string exe = kv.Key;
                string title = kv.Value.Length > 50 ? kv.Value.Substring(0, 50) + "..." : kv.Value;
                menu.Items.Add(exe + "   (" + title + ")", null, delegate { AddProgram(exe); });
            }
            if (menu.Items.Count == 0) menu.Items.Add("(no windows found)").Enabled = false;
            menu.Closed += delegate { BeginInvoke((Action)menu.Dispose); };
            menu.Show(anchor, new Point(0, anchor.Height));
        }

        private void BrowseProgram()
        {
            using (var dlg = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe", Title = "Choose a program" })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK) AddProgram(Path.GetFileName(dlg.FileName));
            }
        }

        private void AddProgram(string exe)
        {
            foreach (object item in _programs.Items)
                if (string.Equals((string)item, exe, StringComparison.OrdinalIgnoreCase)) return;
            _programs.Items.Add(exe);
        }

        private void OnSave()
        {
            var cfg = new Config { EdidId = _original.EdidId, Model = _model };
            cfg.InputCodesDetected.AddRange(_inputs);
            var used = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Keep settings for inputs that aren't shown (e.g. missing from the latest detection).
            var shown = new HashSet<string>(_rows.Select(r => r.Input.Key), StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> kv in _original.Hotkeys)
                if (!shown.Contains(kv.Key)) cfg.Hotkeys[kv.Key] = kv.Value;
            foreach (string h in _original.Hidden)
                if (!shown.Contains(h)) cfg.Hidden.Add(h);

            foreach (Row row in _rows)
            {
                if (!row.Show.Checked) cfg.Hidden.Add(row.Input.Key);
                string hotkey = row.Hotkey.Hotkey;
                if (hotkey.Length == 0 || !row.Hotkey.Enabled) continue;

                if (row.Hotkey.Modifiers == 0 && IsTypingKey(row.Hotkey.Key))
                {
                    Fail(row.Hotkey, hotkey + " on its own would block normal typing. Add Ctrl, Alt, Shift or Win.");
                    return;
                }
                string other;
                if (used.TryGetValue(hotkey, out other))
                {
                    Fail(row.Hotkey, hotkey + " is already used for " + other + ".");
                    return;
                }
                used[hotkey] = row.Input.Label;
                cfg.Hotkeys[row.Input.Key] = hotkey;
            }

            foreach (object item in _programs.Items) cfg.BlockPrograms.Add((string)item);

            Result = cfg;
            Close();
        }

        private static bool IsTypingKey(Keys key)
        {
            return (key >= Keys.A && key <= Keys.Z) || (key >= Keys.D0 && key <= Keys.D9) ||
                   (key >= Keys.NumPad0 && key <= Keys.Divide) || key == Keys.Space || key == Keys.Return ||
                   key == Keys.Back || key == Keys.Tab || (key >= Keys.Oem1 && key <= Keys.Oem102);
        }

        private void Fail(Control control, string message)
        {
            MessageBox.Show(this, message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            control.Focus();
        }
    }

    // Read-only text box that records the key combination pressed while it has focus.
    internal class HotkeyBox : TextBox
    {
        [DllImport("user32.dll")]
        private static extern short GetKeyState(int vk);

        public uint Modifiers { get; private set; }
        public Keys Key { get; private set; }

        public HotkeyBox()
        {
            ReadOnly = true;
            BackColor = SystemColors.Window;
            ShortcutsEnabled = false;
            Cursor = Cursors.Hand;
        }

        public string Hotkey
        {
            get { return Key == Keys.None ? "" : Config.FormatHotkey(Modifiers, Key); }
            set
            {
                uint m;
                Keys k;
                if (Config.TryParseHotkey(value, out m, out k)) { Modifiers = m; Key = k; }
                else { Modifiers = 0; Key = Keys.None; }
                ShowCurrent();
            }
        }

        private void ShowCurrent()
        {
            Text = Key == Keys.None ? "(none)" : Hotkey;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            uint mods = 0;
            if ((keyData & Keys.Control) != 0) mods |= Config.MOD_CONTROL;
            if ((keyData & Keys.Alt) != 0) mods |= Config.MOD_ALT;
            if ((keyData & Keys.Shift) != 0) mods |= Config.MOD_SHIFT;
            if (GetKeyState(0x5B) < 0 || GetKeyState(0x5C) < 0) mods |= Config.MOD_WIN;

            // Let Tab/Enter/Escape keep their normal dialog meaning.
            if (mods == 0 && (key == Keys.Tab || key == Keys.Return || key == Keys.Escape))
                return base.ProcessCmdKey(ref msg, keyData);

            if (mods == 0 && (key == Keys.Back || key == Keys.Delete))
            {
                Modifiers = 0;
                Key = Keys.None;
                ShowCurrent();
                return true;
            }

            switch (key)
            {
                case Keys.ControlKey: case Keys.LControlKey: case Keys.RControlKey:
                case Keys.ShiftKey: case Keys.LShiftKey: case Keys.RShiftKey:
                case Keys.Menu: case Keys.LMenu: case Keys.RMenu:
                case Keys.LWin: case Keys.RWin:
                    Text = Config.FormatHotkey(mods, Keys.None); // modifiers held, waiting for the key
                    return true;
            }

            Modifiers = mods;
            Key = key;
            ShowCurrent();
            return true;
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            ShowCurrent();
            base.OnKeyUp(e);
        }

        protected override void OnLostFocus(EventArgs e)
        {
            ShowCurrent();
            base.OnLostFocus(e);
        }
    }
}
