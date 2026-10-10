using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace LgInputSwitcher
{
    internal static class Foreground
    {
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        // File name of the foreground window's process, e.g. "Overwatch.exe", or null.
        public static string ExeName()
        {
            uint pid;
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero || GetWindowThreadProcessId(hwnd, out pid) == 0) return null;

            IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process != IntPtr.Zero)
            {
                try
                {
                    var sb = new StringBuilder(1024);
                    uint size = (uint)sb.Capacity;
                    if (QueryFullProcessImageName(process, 0, sb, ref size)) return Path.GetFileName(sb.ToString());
                }
                finally
                {
                    CloseHandle(process);
                }
            }

            // Protected (anti-cheat) or elevated games can refuse OpenProcess; the system process
            // list still has their name, so a blocked game isn't mistaken for "no program".
            try
            {
                using (Process p = Process.GetProcessById((int)pid))
                    return p.ProcessName + ".exe";
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
