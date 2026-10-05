using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO.Ports;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace VestibularJoystickSim
{
    // Engineering connection monitor. It has no output/arm/calibration API.
    // Status describes firmware state; it does not measure analog delivery.
    internal sealed class VmocionUsbStatus
    {
        public string Firmware;
        public uint Uptime;
        public bool Armed, Carrier, FaultHealthy, FaultLatched;
        public bool Calibration, Credentials, Replay, Permission;
        public bool Green, Blue;
        public string Raw;
        public long ReceivedTicks;
        public bool OutputOff { get { return !Armed && !Carrier; } }
        public bool Fresh { get { return (Stopwatch.GetTimestamp() - ReceivedTicks) / (double)Stopwatch.Frequency < 1.0; } }
        public string Summary
        {
            get
            {
                return Firmware + "; output " + (OutputOff ? "reported off" : "ACTIVE") +
                    "; fault " + (!FaultHealthy || FaultLatched ? "present" : "clear") +
                    "; calibration " + (Calibration ? "valid" : "unqualified") +
                    "; output control unavailable in this engineering build";
            }
        }
    }

    internal static class VmocionUsbProtocol
    {
        public static uint Crc32(byte[] bytes, int count)
        {
            uint crc = 0xffffffff;
            for (int i = 0; i < count; ++i)
            {
                crc ^= bytes[i];
                for (int b = 0; b < 8; ++b) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320U : 0U);
            }
            return crc ^ 0xffffffff;
        }
        private static uint Read32(byte[] b, int offset)
        {
            return (uint)b[offset] | ((uint)b[offset + 1] << 8) | ((uint)b[offset + 2] << 16) | ((uint)b[offset + 3] << 24);
        }
        private static void Write32(byte[] b, int offset, uint v)
        {
            for (int i = 0; i < 4; ++i) b[offset + i] = (byte)(v >> (8 * i));
        }
        public static byte[] StatusRequest(uint nonce)
        {
            if (nonce == 0) throw new ArgumentException("Nonzero status nonce required");
            byte[] b = new byte[32]; b[0] = 0x56; b[1] = 0x4d; b[2] = 1; b[3] = 1;
            Write32(b, 4, nonce); Write32(b, 28, Crc32(b, 28)); return b;
        }
        public static VmocionUsbStatus GuardedStatus(byte[] b, uint nonce)
        {
            if (nonce == 0 || b.Length != 32 || b[0] != 0x56 || b[1] != 0x4d || b[2] != 1 || b[3] != 0x81 ||
                Read32(b, 4) != nonce || b[8] != 16 || b[9] != 0 || b[10] != 0 || b[11] != 0 ||
                Read32(b, 28) != Crc32(b, 28)) throw new FormatException("Invalid guarded-v1 status header, nonce or CRC");
            foreach (int i in new int[] { 12, 13, 14, 15, 16, 18 })
                if (b[i] > 1) throw new FormatException("Nonboolean guarded status");
            if (b[17] != 1 || (b[19] & ~7) != 0 || ((b[19] & 2) == 0 && Read32(b, 20) != 0) ||
                (b[16] != 0 && b[15] == 0)) throw new FormatException("Inconsistent guarded status");
            return new VmocionUsbStatus { Firmware = "VMocion Minimal guarded-v1", Uptime = Read32(b, 24),
                Calibration = b[12] != 0, FaultHealthy = b[13] != 0, FaultLatched = b[14] != 0,
                Armed = b[15] != 0, Carrier = b[16] != 0, Credentials = b[18] != 0,
                Replay = (b[19] & 1) != 0, Permission = (b[19] & 4) != 0,
                ReceivedTicks = Stopwatch.GetTimestamp(), Raw = BitConverter.ToString(b).Replace("-", "") };
        }
        public static VmocionUsbStatus BenchStatus(string line)
        {
            string[] parts = line.Trim().Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            bool nominal2k = parts.Length > 0 && parts[0] == "VMOCION_NOMINAL_2K_AB_V1";
            if (parts.Length < 2 || (!nominal2k && parts[0] != "VMOCION_BENCH_100K_PAIR_V3" && parts[0] != "VMOCION_BENCH_100K_PAIR_V4" && parts[0] != "VMOCION_BENCH_100K_PAIR_V5"))
                throw new FormatException("Unsupported bench firmware");
            Dictionary<string, string> fields = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 1; i < parts.Length; ++i)
            {
                string[] p = parts[i].Split('=');
                if (p.Length != 2 || fields.ContainsKey(p[0])) throw new FormatException("Malformed bench status");
                fields.Add(p[0], p[1]);
            }
            foreach (string key in new string[] { "active", "arm_pin", "carrier_pin", "fault_n", "fault_latched", "green_led_on", "blue_led_on" })
                if (!fields.ContainsKey(key) || (fields[key] != "0" && fields[key] != "1")) throw new FormatException("Missing/nonboolean bench status");
            uint uptime;
            if (!fields.ContainsKey("uptime_ms") || !uint.TryParse(fields["uptime_ms"], NumberStyles.None, CultureInfo.InvariantCulture, out uptime) ||
                !fields.ContainsKey("pair") || fields["pair"] != "J2_1_2" ||
                !fields.ContainsKey("assumed_load_ohms") || fields["assumed_load_ohms"] != (nominal2k ? "2000" : "100000") ||
                (nominal2k && (!fields.ContainsKey("nominal_only") || fields["nominal_only"] != "1" ||
                    !fields.ContainsKey("calibration_present") || fields["calibration_present"] != "0")))
                throw new FormatException("Unexpected bench fixture/status");
            return new VmocionUsbStatus { Firmware = parts[0], Uptime = uptime,
                Armed = fields["arm_pin"] == "1" || fields["active"] == "1", Carrier = fields["carrier_pin"] == "1",
                FaultHealthy = fields["fault_n"] == "1", FaultLatched = fields["fault_latched"] == "1",
                Green = fields["green_led_on"] == "1", Blue = fields["blue_led_on"] == "1",
                ReceivedTicks = Stopwatch.GetTimestamp(), Raw = line };
        }
        public static bool Advances(uint previous, uint current)
        {
            uint delta = unchecked(current - previous); return delta != 0 && delta < 0x80000000U;
        }
        public static bool SupportedHardware(string pnp)
        {
            string text = (pnp ?? "").ToUpperInvariant();
            return text.Contains("VID_2886&PID_8045") || text.Contains("VID_2886&PID_0145");
        }
        public static string PortInName(string name)
        {
            int start = (name ?? "").LastIndexOf("(COM", StringComparison.OrdinalIgnoreCase);
            if (start < 0) return null;
            int end = name.IndexOf(')', start); if (end < 0) return null;
            string port = name.Substring(start + 1, end - start - 1);
            uint number;
            return uint.TryParse(port.Substring(3), NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0 ? port : null;
        }
        public static bool SelfTest()
        {
            try
            {
                byte[] request = StatusRequest(0x12345678);
                if (BitConverter.ToString(request).Replace("-", "").ToLowerInvariant() !=
                    "564d01017856341200000000000000000000000000000000000000006c266190") return false;
                byte[] response = new byte[32]; response[0] = 0x56; response[1] = 0x4d; response[2] = 1; response[3] = 0x81;
                Write32(response, 4, 0x12345678); response[8] = 16; response[13] = 1; response[17] = 1;
                Write32(response, 24, 5000); Write32(response, 28, Crc32(response, 28));
                VmocionUsbStatus s = GuardedStatus(response, 0x12345678);
                if (!s.OutputOff || !s.FaultHealthy || s.Calibration || s.Credentials || s.Permission || s.Uptime != 5000) return false;
                for (int i = 0; i < response.Length; ++i)
                {
                    byte[] bad = (byte[])response.Clone(); bad[i] ^= 1;
                    try { GuardedStatus(bad, 0x12345678); return false; } catch (FormatException) { }
                }
                try { GuardedStatus(response, 9); return false; } catch (FormatException) { }
                response[15] = 1; Write32(response, 28, Crc32(response, 28));
                if (GuardedStatus(response, 0x12345678).OutputOff) return false;
                response[15] = 0; response[16] = 1; Write32(response, 28, Crc32(response, 28));
                try { GuardedStatus(response, 0x12345678); return false; } catch (FormatException) { }
                string bench = "VMOCION_BENCH_100K_PAIR_V4 active=0 arm_pin=0 carrier_pin=0 fault_n=1 fault_latched=0 green_led_on=0 blue_led_on=1 assumed_load_ohms=100000 pair=J2_1_2 uptime_ms=5000";
                if (!BenchStatus(bench).OutputOff || !BenchStatus(bench).Blue || !BenchStatus(bench.Replace("V4", "V5")).OutputOff) return false;
                string nominal = bench.Replace("VMOCION_BENCH_100K_PAIR_V4", "VMOCION_NOMINAL_2K_AB_V1").Replace("100000", "2000") + " nominal_only=1 calibration_present=0";
                if (!BenchStatus(nominal).OutputOff || BenchStatus(nominal).Calibration) return false;
                foreach (string bad in new string[] { nominal.Replace("2000", "100000"), nominal.Replace("nominal_only=1", "nominal_only=0"), nominal.Replace("calibration_present=0", "calibration_present=1"), nominal.Replace(" calibration_present=0", "") })
                    try { BenchStatus(bad); return false; } catch (FormatException) { }
                foreach (string bad in new string[] { bench + " active=0", bench.Replace("fault_n=1", "fault_n=2"), bench.Replace("100000", "5000"), bench.Replace("V4", "V99"), bench.Replace(" uptime_ms=5000", "") })
                    try { BenchStatus(bad); return false; } catch (FormatException) { }
                return Advances(0xfffffffe, 2) && !Advances(5000, 5000) && !Advances(5000, 4) &&
                    SupportedHardware("USB\\VID_2886&PID_8045\\test") && !SupportedHardware("USB\\VID_2886&PID_0045") &&
                    !SupportedHardware("USB\\VID_0403&PID_6001") && PortInName("Device (COM10)") == "COM10" && PortInName("COM1 text") == null;
            }
            catch { return false; }
        }
    }

    internal sealed class VmocionUsbMonitor : IDisposable
    {
        private Thread worker;
        private volatile bool stopping;
        private volatile VmocionUsbStatus latest;
        private volatile string error;
        private int sampleCount;
        public string PortName { get; private set; }
        public bool IsOpen { get { return !stopping && error == null; } }
        public string Error { get { return error; } }
        public VmocionUsbStatus Latest { get { return latest; } }
        public bool Verified { get { VmocionUsbStatus s = latest; return sampleCount >= 2 && s != null && s.Fresh && s.OutputOff && IsOpen; } }
        public VmocionUsbMonitor(string name)
        {
            if (VmocionUsbProtocol.PortInName("Device (" + name + ")") != name) throw new ArgumentException("A Windows COM port is required.");
            PortName = name;
            worker = new Thread(Run); worker.IsBackground = true; worker.Name = "VMocion USB status"; worker.Start();
        }
        public static bool IsSupportedPort(string name)
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'"))
            using (ManagementObjectCollection devices = searcher.Get())
                foreach (ManagementObject d in devices)
                    if (string.Equals(VmocionUsbProtocol.PortInName(d["Name"] as string), name, StringComparison.OrdinalIgnoreCase) &&
                        VmocionUsbProtocol.SupportedHardware(d["PNPDeviceID"] as string)) return true;
            return false;
        }
        private void Run()
        {
            try
            {
                if (!IsSupportedPort(PortName)) throw new InvalidOperationException("Select the XIAO application USB port; legacy FTDI and bootloader ports are unsupported.");
                if (stopping) return;
                using (SerialPort link = new SerialPort(PortName, 115200))
                {
                    link.ReadTimeout = 50; link.WriteTimeout = 250; link.DtrEnable = true; link.RtsEnable = false;
                    link.Open(); link.DiscardInBuffer();
                    VmocionUsbStatus first = null;
                    try { first = ReadBench(link); } catch (TimeoutException) { }
                    bool bench = first != null;
                    VmocionUsbStatus previous = first;
                    while (!stopping)
                    {
                        VmocionUsbStatus next = first ?? (bench ? ReadBench(link) : ReadGuarded(link)); first = null;
                        if (!next.OutputOff) throw new InvalidOperationException("Firmware reports active output; connection closed. Disconnect PCB power if output persists.");
                        if (sampleCount > 0 && !VmocionUsbProtocol.Advances(previous.Uptime, next.Uptime))
                            throw new InvalidOperationException("Firmware reset or nonadvancing status; reconnect required.");
                        previous = next; latest = next; Interlocked.Increment(ref sampleCount);
                        for (int i = 0; i < 25 && !stopping; ++i) Thread.Sleep(10);
                    }
                }
            }
            catch (Exception ex) { if (!stopping) error = ex.Message; }

        }
        private static VmocionUsbStatus ReadBench(SerialPort link)
        {
            link.Write("STATUS\n"); Stopwatch deadline = Stopwatch.StartNew(); StringBuilder line = new StringBuilder();
            while (deadline.ElapsedMilliseconds < 600)
            {
                int c; try { c = link.ReadByte(); } catch (TimeoutException) { continue; }
                if (c == '\n')
                {
                    string text = line.ToString().Trim(); line.Length = 0;
                    if (text.StartsWith("VMOCION_BENCH_", StringComparison.Ordinal) || text.StartsWith("VMOCION_NOMINAL_", StringComparison.Ordinal)) return VmocionUsbProtocol.BenchStatus(text);
                }
                else if (c >= 32 && c <= 126) { if (line.Length >= 1024) throw new FormatException("Oversize bench status"); line.Append((char)c); }
                else if (c != '\r') line.Length = 0;
            }
            throw new TimeoutException("No supported bench status");
        }
        private static VmocionUsbStatus ReadGuarded(SerialPort link)
        {
            byte[] random = new byte[4]; uint nonce;
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
                do { rng.GetBytes(random); nonce = BitConverter.ToUInt32(random, 0); } while (nonce == 0);
            byte[] request = VmocionUsbProtocol.StatusRequest(nonce); link.Write(request, 0, request.Length);
            Stopwatch deadline = Stopwatch.StartNew(); List<byte> buffer = new List<byte>();
            while (deadline.ElapsedMilliseconds < 600)
            {
                int c; try { c = link.ReadByte(); } catch (TimeoutException) { continue; }
                buffer.Add((byte)c);
                if (buffer.Count == 32)
                {
                    try { return VmocionUsbProtocol.GuardedStatus(buffer.ToArray(), nonce); }
                    catch (FormatException) { buffer.RemoveAt(0); }
                }
            }
            throw new TimeoutException("No valid guarded-v1 status; legacy protocol is not accepted.");
        }
        public void Close()
        {
            stopping = true;
            if (worker != null && worker != Thread.CurrentThread) worker.Join(1600);
        }
        public void Dispose() { Close(); }
    }
}
