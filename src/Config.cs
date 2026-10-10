using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace LgInputSwitcher
{
    // An input as reported by the monitor (standard MCCS VCP 0x60 code) plus the user's settings for it.
    internal class InputDef
    {
        public byte Code;       // VCP 0x60 value, e.g. 0x11 = HDMI 1
        public string Key;      // "hdmi1" (used on the command line and in the ini)
        public string Label;    // "HDMI 1"
        public byte? LgValue;   // value for LG's 0xF4 switch command; null if unknown
        public string Hotkey;   // "Ctrl+Alt+2" or ""
        public bool Hidden;     // left out of the tray menu, no hotkey
    }

    // Names for VCP 0x60 input codes, and the matching value for LG's proprietary 0xF4 switch command.
    internal static class InputCodes
    {
        private static readonly Dictionary<byte, string[]> Known = new Dictionary<byte, string[]>
        {
            // code: key, label, LG 0xF4 value
            { 0x01, new[] { "vga1", "VGA 1", null } },
            { 0x02, new[] { "vga2", "VGA 2", null } },
            { 0x03, new[] { "dvi1", "DVI 1", null } },
            { 0x04, new[] { "dvi2", "DVI 2", null } },
            { 0x0F, new[] { "dp1", "DisplayPort 1", "D0" } },
            { 0x10, new[] { "dp2", "DisplayPort 2", "D1" } },
            { 0x11, new[] { "hdmi1", "HDMI 1", "90" } },
            { 0x12, new[] { "hdmi2", "HDMI 2", "91" } },
            { 0x1B, new[] { "usbc", "USB-C", null } },
        };

        public static InputDef Describe(byte code)
        {
            string[] k;
            if (Known.TryGetValue(code, out k))
                return new InputDef { Code = code, Key = k[0], Label = k[1], LgValue = k[2] == null ? (byte?)null : byte.Parse(k[2], NumberStyles.HexNumber) };
            return new InputDef { Code = code, Key = string.Format("input{0:x2}", code), Label = string.Format("Input 0x{0:X2}", code) };
        }
    }

    // lg-input-switcher.ini, next to the exe:
    //   [monitor]  edid, model and inputs (detected from the monitor)
    //   [hotkeys]  hdmi1 = Ctrl+Alt+2
    //   [menu]     hide = dp2
    //   [block]    programs = Overwatch.exe
    internal class Config
    {
        public string EdidId = "GSM5C55";
        public string Model = "";
        public readonly List<byte> InputCodesDetected = new List<byte>();
        public readonly Dictionary<string, string> Hotkeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> BlockPrograms = new List<string>();

        public static string PathNextToExe
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lg-input-switcher.ini"); }
        }

        public static Config Load()
        {
            string path = PathNextToExe;
            if (File.Exists(path)) return Parse(File.ReadAllLines(path));

            // First run: default shortcuts; the inputs themselves are detected from the monitor.
            var cfg = new Config();
            cfg.Hotkeys["dp1"] = "Ctrl+Alt+1";
            cfg.Hotkeys["hdmi1"] = "Ctrl+Alt+2";
            cfg.Hotkeys["hdmi2"] = "Ctrl+Alt+3";
            return cfg;
        }

        // The monitor's inputs combined with the user's hotkey / hidden settings.
        public List<InputDef> Inputs
        {
            get
            {
                var list = new List<InputDef>();
                foreach (byte code in InputCodesDetected)
                {
                    InputDef d = InputCodes.Describe(code);
                    string hk;
                    d.Hotkey = Hotkeys.TryGetValue(d.Key, out hk) ? hk : "";
                    d.Hidden = Hidden.Contains(d.Key);
                    list.Add(d);
                }
                return list;
            }
        }

        public InputDef Find(string key)
        {
            if (key.Equals("dp", StringComparison.OrdinalIgnoreCase)) key = "dp1";
            if (key.Equals("hdmi", StringComparison.OrdinalIgnoreCase)) key = "hdmi1";
            return Inputs.Find(i => i.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        }

        public void Save()
        {
            var sb = new StringBuilder();
            sb.AppendLine("; " + AppInfo.Name + " settings. Edit from the tray menu > Settings, or by hand and then tray menu > Reload settings file.");
            sb.AppendLine();
            sb.AppendLine("[monitor]");
            sb.AppendLine("; Detected from the monitor (EDID and DDC capabilities). Use Settings > Detect again to refresh.");
            sb.AppendLine("edid = " + EdidId);
            sb.AppendLine("model = " + Model);
            sb.AppendLine("inputs = " + string.Join(" ", InputCodesDetected.Select(c => c.ToString("X2"))));
            sb.AppendLine();
            sb.AppendLine("[hotkeys]");
            sb.AppendLine("; input = Ctrl/Alt/Shift/Win + a key name (1, F9, Home, NumPad1, ...)");
            foreach (KeyValuePair<string, string> kv in Hotkeys)
                if (kv.Value.Length > 0) sb.AppendLine(kv.Key + " = " + kv.Value);
            sb.AppendLine();
            sb.AppendLine("[menu]");
            sb.AppendLine("; Inputs left out of the tray menu (space-separated).");
            sb.AppendLine("hide = " + string.Join(" ", Hidden.OrderBy(h => h)));
            sb.AppendLine();
            sb.AppendLine("[block]");
            sb.AppendLine("; Shortcuts do nothing while one of these programs is the focused window (comma-separated).");
            sb.AppendLine("programs = " + string.Join(", ", BlockPrograms));
            File.WriteAllText(PathNextToExe, sb.ToString());
        }

        public static Config Parse(string[] lines)
        {
            var cfg = new Config();
            string section = "";
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    section = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant();
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string val = line.Substring(eq + 1).Trim();

                if (section == "monitor")
                {
                    if (key == "edid") cfg.EdidId = val;
                    else if (key == "model") cfg.Model = val;
                    else if (key == "inputs")
                    {
                        // Skip malformed codes rather than failing the whole file.
                        byte code;
                        foreach (string c in val.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
                            if (byte.TryParse(c, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code) && !cfg.InputCodesDetected.Contains(code))
                                cfg.InputCodesDetected.Add(code);
                    }
                }
                else if (section == "hotkeys" && val.Length > 0)
                {
                    cfg.Hotkeys[key] = val;
                }
                else if (section == "menu" && key == "hide")
                {
                    foreach (string h in val.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
                        cfg.Hidden.Add(h);
                }
                else if (section == "block" && key == "programs")
                {
                    foreach (string p in val.Split(','))
                        if (p.Trim().Length > 0) cfg.BlockPrograms.Add(p.Trim());
                }
            }
            return cfg;
        }

        // Matches "Overwatch.exe" or "Overwatch" against a process image name.
        public bool IsBlocked(string exeName)
        {
            if (string.IsNullOrEmpty(exeName)) return false;
            string bare = Path.GetFileNameWithoutExtension(exeName);
            return BlockPrograms.Exists(p =>
                p.Equals(exeName, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileNameWithoutExtension(p).Equals(bare, StringComparison.OrdinalIgnoreCase));
        }

        public static byte ParseByte(string s)
        {
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return byte.Parse(s.Substring(2), NumberStyles.HexNumber);
            return byte.Parse(s);
        }

        public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8;

        // "Ctrl+Alt+1" -> (MOD_CONTROL | MOD_ALT, Keys.D1)
        public static bool TryParseHotkey(string s, out uint modifiers, out Keys key)
        {
            modifiers = 0;
            key = Keys.None;
            if (string.IsNullOrEmpty(s)) return false;
            foreach (string token in s.Split('+'))
            {
                string t = token.Trim();
                switch (t.ToLowerInvariant())
                {
                    case "ctrl": case "control": modifiers |= MOD_CONTROL; break;
                    case "alt": modifiers |= MOD_ALT; break;
                    case "shift": modifiers |= MOD_SHIFT; break;
                    case "win": case "windows": modifiers |= MOD_WIN; break;
                    default:
                        if (t.Length == 1 && char.IsDigit(t[0])) t = "D" + t;
                        try { key = (Keys)Enum.Parse(typeof(Keys), t, true); }
                        catch (ArgumentException) { return false; }
                        break;
                }
            }
            return key != Keys.None;
        }

        // (MOD_CONTROL | MOD_ALT, Keys.D1) -> "Ctrl+Alt+1"
        public static string FormatHotkey(uint modifiers, Keys key)
        {
            var sb = new StringBuilder();
            if ((modifiers & MOD_CONTROL) != 0) sb.Append("Ctrl+");
            if ((modifiers & MOD_ALT) != 0) sb.Append("Alt+");
            if ((modifiers & MOD_SHIFT) != 0) sb.Append("Shift+");
            if ((modifiers & MOD_WIN) != 0) sb.Append("Win+");
            if (key == Keys.None) return sb.ToString();
            string name = key.ToString();
            if (key >= Keys.D0 && key <= Keys.D9) name = name.Substring(1);
            return sb.Append(name).ToString();
        }
    }
}
