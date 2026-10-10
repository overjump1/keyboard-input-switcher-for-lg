using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;

namespace LgInputSwitcher
{
    internal class DisplayInfo
    {
        public IntPtr Gpu;
        public uint DisplayId;
        public string EdidId;   // e.g. "GSM5C55"
        public string Name;     // monitor name descriptor from EDID, e.g. "LG ULTRAGEAR"
        public bool IsConnected;
    }

    // What was read from the monitor. Inputs/Model are null if detection failed (see Error).
    internal class MonitorReading
    {
        public string Model;
        public List<byte> Inputs;
        public string Error;
    }

    // What the tray app needs from the monitor (lets tests substitute a fake).
    internal interface IMonitorSwitcher
    {
        string EdidId { get; }
        MonitorReading Read();
        void SwitchTo(InputDef input);
    }

    // Switches LG monitor inputs using LG's non-standard DDC command: VCP 0xF4 sent with
    // source address 0x50 (standard DDC/CI uses 0x51, which LG ignores for input select).
    internal class LgSwitcher : IMonitorSwitcher
    {
        private const byte DdcWriteAddress = 0x6E; // 0x37 << 1
        private const byte DdcReadAddress = 0x6F;
        private const byte LgSourceAddress = 0x50;
        private const byte StdSourceAddress = 0x51;
        private const byte LgInputVcp = 0xF4;

        private readonly string _edidId;
        private readonly object _io = new object(); // one DDC transaction at a time
        private DisplayInfo _cached;

        public string EdidId { get { return _edidId; } }

        public LgSwitcher(string edidId)
        {
            _edidId = edidId.ToUpperInvariant();
            Nvapi.Initialize();
        }

        // uncached forces the driver to re-probe connectors (slower; used after a failure).
        public static List<DisplayInfo> ListDisplays(bool uncached)
        {
            Nvapi.Initialize();
            var result = new List<DisplayInfo>();
            uint flags = uncached ? Nvapi.NV_GPU_CONNECTED_IDS_FLAG_UNCACHED : 0;
            foreach (IntPtr gpu in Nvapi.EnumPhysicalGPUs())
            {
                foreach (var id in Nvapi.GetConnectedDisplayIds(gpu, flags))
                {
                    var d = new DisplayInfo { Gpu = gpu, DisplayId = id.displayId, IsConnected = (id.flags & (1 << 6)) != 0 };
                    try
                    {
                        byte[] edid = Nvapi.GetEDID(gpu, id.displayId);
                        d.EdidId = ParseEdidId(edid);
                        d.Name = ParseEdidName(edid);
                    }
                    catch (NvapiException) { }
                    result.Add(d);
                }
            }
            return result;
        }

        // Finds the configured monitor. The port is cached (display IDs are stable) and only
        // looked up again when refresh is set, i.e. after an I2C transfer failed. Falls back to
        // the last known port when the driver no longer reports the monitor.
        public DisplayInfo FindMonitor(bool refresh = false)
        {
            if (_cached != null && !refresh) return _cached;
            try
            {
                foreach (var d in ListDisplays(refresh))
                {
                    if (string.Equals(d.EdidId, _edidId, StringComparison.OrdinalIgnoreCase))
                    {
                        _cached = d;
                        return d;
                    }
                }
            }
            catch (NvapiException)
            {
                if (_cached == null) throw;
            }
            if (_cached != null) return _cached;
            throw new NvapiException("Monitor " + _edidId + " not found on an NVIDIA GPU");
        }

        // Model name and the inputs the monitor reports (VCP 0x60 values in its capabilities string).
        public void Detect(out string model, out List<byte> inputs)
        {
            DisplayInfo d = FindMonitor();
            string caps = ReadCapabilities();
            Match m = Regex.Match(caps, @"model\(([^)]*)\)");
            model = (d.Name ?? d.EdidId) + (m.Success ? " (" + m.Groups[1].Value.Trim() + ")" : "");

            inputs = new List<byte>();
            Match v = Regex.Match(caps, @"(?<![0-9A-Fa-f])60\(([0-9A-Fa-f ]*)\)");
            if (!v.Success) throw new NvapiException("Monitor doesn't report its inputs: " + caps);
            foreach (string c in v.Groups[1].Value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                inputs.Add(byte.Parse(c, NumberStyles.HexNumber));
        }

        public MonitorReading Read()
        {
            var r = new MonitorReading();
            try
            {
                string model;
                List<byte> inputs;
                Detect(out model, out inputs);
                r.Model = model;
                r.Inputs = inputs;
            }
            catch (Exception e)
            {
                r.Error = e.Message;
            }
            return r;
        }

        // Always sends the switch: the LG's VCP 0x60 keeps reporting the DisplayPort input it's
        // queried over even while it shows HDMI, so the current input can't be known.
        public void SwitchTo(InputDef input)
        {
            if (input.LgValue == null)
                throw new NvapiException("Don't know LG's switch code for " + input.Label);
            SwitchInput(input.LgValue.Value);
        }

        public DisplayInfo SwitchInput(byte value)
        {
            lock (_io) return SwitchInputLocked(value);
        }

        private DisplayInfo SwitchInputLocked(byte value)
        {
            // 0x84 = 0x80 | 4 data bytes, 0x03 = Set VCP Feature, VCP code, value high, value low, checksum
            byte[] data = { 0x84, 0x03, LgInputVcp, 0x00, value, 0x00 };
            data[data.Length - 1] = Checksum(DdcWriteAddress, LgSourceAddress, data);

            NvapiException last = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    DisplayInfo d = FindMonitor(attempt > 0);
                    Nvapi.I2C(true, d.Gpu, d.DisplayId, DdcWriteAddress, new[] { LgSourceAddress }, data);
                    return d;
                }
                catch (NvapiException e)
                {
                    Log.Write("I2C write attempt {0} failed: {1}", attempt + 1, e.Message);
                    last = e;
                    Thread.Sleep(100);
                }
            }
            throw last;
        }

        // DDC/CI "Get VCP Feature". Returns { current, max }.
        public int[] ReadVcp(byte vcp)
        {
            // Reply: src(0x6E) len(0x88) 0x02 result vcp type maxH maxL curH curL chk
            byte[] reply = Request(StdSourceAddress, new byte[] { 0x01, vcp }, 11, 40);
            if (reply[2] != 0x02 || reply[4] != vcp)
                throw new NvapiException("Unexpected DDC reply: " + BitConverter.ToString(reply));
            if (reply[3] != 0x00)
                throw new NvapiException(string.Format("Monitor reports VCP 0x{0:X2} unsupported", vcp));
            return new[] { (reply[8] << 8) | reply[9], (reply[6] << 8) | reply[7] };
        }

        // DDC/CI "Capabilities Request", read in fragments, e.g. "(prot(monitor)type(LCD)model(...)vcp(10 12 60(0F 11 12) ...))".
        public string ReadCapabilities()
        {
            lock (_io) return ReadCapabilitiesLocked();
        }

        private string ReadCapabilitiesLocked()
        {
            var sb = new System.Text.StringBuilder();
            for (int offset = 0; offset < 4096; )
            {
                // Reply: src len 0xE3 offH offL data... chk (up to 32 data bytes)
                byte[] reply = Request(StdSourceAddress, new byte[] { 0xF3, (byte)(offset >> 8), (byte)offset }, 38, 60);
                int len = (reply[1] & 0x7F) - 3;
                if (reply[2] != 0xE3 || len < 0 || len > 32 || ((reply[3] << 8) | reply[4]) != offset)
                    throw new NvapiException("Unexpected capabilities reply: " + BitConverter.ToString(reply));
                if (len == 0) break;
                for (int i = 0; i < len; i++)
                    if (reply[5 + i] >= 0x20 && reply[5 + i] < 0x7F) sb.Append((char)reply[5 + i]);
                offset += len;
            }
            return sb.ToString();
        }

        // Sends a DDC/CI request and reads the reply. A length byte of 0x80 is a null message
        // (monitor busy) and a bad checksum is a garbled read; both are retried.
        private byte[] Request(byte source, byte[] payload, int replyLength, int delayMs)
        {
            lock (_io) return RequestLocked(source, payload, replyLength, delayMs);
        }

        private byte[] RequestLocked(byte source, byte[] payload, int replyLength, int delayMs)
        {
            byte[] data = new byte[payload.Length + 2];
            data[0] = (byte)(0x80 | payload.Length);
            Array.Copy(payload, 0, data, 1, payload.Length);
            data[data.Length - 1] = Checksum(DdcWriteAddress, source, data);

            byte[] reply = new byte[replyLength];
            bool transferFailed = false;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    DisplayInfo d = FindMonitor(transferFailed);
                    Nvapi.I2C(true, d.Gpu, d.DisplayId, DdcWriteAddress, new[] { source }, data);
                    Thread.Sleep(delayMs);
                    Nvapi.I2C(false, d.Gpu, d.DisplayId, DdcReadAddress, null, reply);
                    transferFailed = false;
                }
                catch (NvapiException)
                {
                    if (attempt == 4) throw;
                    transferFailed = true;
                    Thread.Sleep(100);
                    continue;
                }
                if (reply[1] != 0x80 && ReplyChecksumOk(reply)) return reply;
                Thread.Sleep(100);
            }
            throw new NvapiException("No valid DDC reply: " + BitConverter.ToString(reply));
        }

        private static bool ReplyChecksumOk(byte[] reply)
        {
            int len = reply[1] & 0x7F;
            if (len + 3 > reply.Length) return false;
            byte chk = 0x50; // replies are checksummed against the host's virtual address 0x50
            for (int i = 0; i < len + 2; i++) chk ^= reply[i];
            return chk == reply[len + 2];
        }

        private static byte Checksum(byte devAddress, byte source, byte[] data)
        {
            byte chk = (byte)(devAddress ^ source);
            for (int i = 0; i < data.Length - 1; i++) chk ^= data[i];
            return chk;
        }

        private static string ParseEdidId(byte[] edid)
        {
            if (edid == null || edid.Length < 12) return null;
            int m = (edid[8] << 8) | edid[9];
            char a = (char)('A' - 1 + ((m >> 10) & 0x1F));
            char b = (char)('A' - 1 + ((m >> 5) & 0x1F));
            char c = (char)('A' - 1 + (m & 0x1F));
            int product = edid[10] | (edid[11] << 8);
            return string.Format("{0}{1}{2}{3:X4}", a, b, c, product);
        }

        private static string ParseEdidName(byte[] edid)
        {
            // Four 18-byte descriptors at 54; tag 0xFC is the monitor name.
            for (int off = 54; off + 18 <= 126 && off + 18 <= edid.Length; off += 18)
            {
                if (edid[off] == 0 && edid[off + 1] == 0 && edid[off + 3] == 0xFC)
                {
                    var sb = new System.Text.StringBuilder();
                    for (int i = off + 5; i < off + 18 && edid[i] != 0x0A; i++) sb.Append((char)edid[i]);
                    return sb.ToString().Trim();
                }
            }
            return null;
        }
    }
}
