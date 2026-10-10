using System;
using System.Threading;
using System.Windows.Forms;

namespace LgInputSwitcher
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
#if CLI
            return RunCli(args);
#else
            if (args.Length > 0) return RunCli(args);

            bool createdNew;
            using (var mutex = new Mutex(true, "LgInputSwitcher.SingleInstance", out createdNew))
            {
                if (!createdNew) return 0;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
            }
            return 0;
#endif
        }

        private static int RunCli(string[] args)
        {
            try
            {
                Config cfg = Config.Load();
                if (args.Length == 0)
                {
                    PrintUsage(cfg);
                    return 2;
                }
                string cmd = args[0].ToLowerInvariant();

                if (cmd == "list")
                {
                    foreach (DisplayInfo d in LgSwitcher.ListDisplays(true))
                    {
                        Console.WriteLine("displayId=0x{0:X8}  edid={1,-8} name={2}{3}{4}",
                            d.DisplayId, d.EdidId ?? "?", d.Name ?? "?",
                            d.IsConnected ? "" : "  (not connected)",
                            string.Equals(d.EdidId, cfg.EdidId, StringComparison.OrdinalIgnoreCase) ? "  <- configured monitor" : "");
                    }
                    return 0;
                }

                var switcher = new LgSwitcher(cfg.EdidId);

                if (cmd == "probe")
                {
                    // Read-only DDC check: reads brightness (VCP 0x10) over the same I2C path.
                    int[] v = switcher.ReadVcp(0x10);
                    Console.WriteLine("DDC OK: brightness {0} / {1}", v[0], v[1]);
                    return 0;
                }

                if (cmd == "caps")
                {
                    Console.WriteLine(switcher.ReadCapabilities());
                    return 0;
                }

                if (cmd == "get" && args.Length >= 2)
                {
                    int[] v = switcher.ReadVcp(Config.ParseByte(args[1]));
                    Console.WriteLine("VCP {0}: current 0x{1:X4} max 0x{2:X4}", args[1], v[0], v[1]);
                    return 0;
                }

                if (cmd.StartsWith("0x"))
                {
                    // Raw LG 0xF4 value, sent as-is (for experimenting with other models).
                    byte raw = Config.ParseByte(cmd);
                    switcher.SwitchInput(raw);
                    Console.WriteLine("Sent LG input value 0x{0:X2}", raw);
                    return 0;
                }

                if (cmd == "detect" || cfg.InputCodesDetected.Count == 0)
                {
                    MonitorReading r = switcher.Read();
                    if (r.Inputs == null) throw new NvapiException(r.Error);
                    cfg.Model = r.Model;
                    cfg.InputCodesDetected.Clear();
                    cfg.InputCodesDetected.AddRange(r.Inputs);
                    cfg.Save();
                    if (cmd == "detect")
                    {
                        Console.WriteLine(r.Model);
                        PrintInputs(cfg);
                        return 0;
                    }
                }

                InputDef input = cfg.Find(cmd);
                if (input == null)
                {
                    PrintUsage(cfg);
                    return 2;
                }
                switcher.SwitchTo(input);
                Console.WriteLine("Switched to " + input.Label);
                return 0;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("Error: " + e.Message);
                return 1;
            }
        }

        private static void PrintUsage(Config cfg)
        {
            Console.WriteLine("Usage: lg-input-switcher-cli <input> | detect | list | probe | caps | get <vcp> | 0xNN");
            Console.WriteLine();
            PrintInputs(cfg);
        }

        private static void PrintInputs(Config cfg)
        {
            Console.WriteLine("Inputs reported by the monitor:");
            foreach (InputDef i in cfg.Inputs)
                Console.WriteLine("  {0,-7} {1,-15} {2,-12} {3}", i.Key, i.Label, i.Hotkey,
                    i.LgValue == null ? "(switch code unknown)" : i.Hidden ? "(hidden from menu)" : "");
        }
    }
}
