using System;
using System.IO;
using System.Text;

namespace LgInputSwitcher
{
    // Small diagnostic log next to the exe (lg-input-switcher.log): shortcut presses, the selected
    // program, blocks, and switch results. Errors are otherwise easy to miss, since Windows hides
    // tray notifications while a game is running.
    internal static class Log
    {
        private const long MaxBytes = 256 * 1024;
        private static readonly object Gate = new object();

        public static string PathNextToExe
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lg-input-switcher.log"); }
        }

        public static void Write(string format, params object[] args)
        {
            try
            {
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + string.Format(format, args) + Environment.NewLine;
                lock (Gate)
                {
                    string path = PathNextToExe;
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > MaxBytes)
                    {
                        // Keep the newer half.
                        string text = File.ReadAllText(path);
                        text = text.Substring(text.Length / 2);
                        int nl = text.IndexOf('\n');
                        File.WriteAllText(path, nl >= 0 ? text.Substring(nl + 1) : text, Encoding.UTF8);
                    }
                    File.AppendAllText(path, line, Encoding.UTF8);
                }
            }
            catch (Exception)
            {
                // Logging must never break switching.
            }
        }
    }
}
