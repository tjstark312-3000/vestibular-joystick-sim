using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using VestibularJoystickSim;

internal static class UsbProtocolTests
{
    private static byte[] Hex(string value)
    {
        byte[] b = new byte[value.Length / 2];
        for (int i = 0; i < b.Length; ++i) b[i] = Convert.ToByte(value.Substring(i * 2, 2), 16);
        return b;
    }
    private static void Require(bool value, string name)
    {
        if (!value) throw new Exception(name); Console.WriteLine("PASS " + name);
    }
    public static int Main(string[] args)
    {
        try
        {
            Require(VmocionUsbProtocol.SelfTest(), "CRC, corrupted frames, nonce, status invariants, COM identity and uptime self-tests");
            Dictionary<string, object> fixture = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(args[0]));
            uint nonce = Convert.ToUInt32(fixture["nonce"]);
            Require(BitConverter.ToString(VmocionUsbProtocol.StatusRequest(nonce)).Replace("-", "").ToLowerInvariant() == (string)fixture["request_hex"], "request matches firmware-accepted Python golden");
            VmocionUsbStatus status = VmocionUsbProtocol.GuardedStatus(Hex((string)fixture["firmware_response_hex"]), nonce);
            Require(status.OutputOff && status.Calibration && status.Credentials && status.Replay && status.Permission && status.Uptime == 5000, "decode response produced by actual portable firmware core with synthetic provisioning");
            uint previous = 0; bool first = true;
            foreach (object raw in (System.Collections.IEnumerable)fixture["bench_status_samples"])
            {
                VmocionUsbStatus bench = VmocionUsbProtocol.BenchStatus((string)raw);
                Require(bench.OutputOff && bench.FaultHealthy && !bench.FaultLatched && bench.Blue && !bench.Green, "decode physical V4 idle status");
                if (!first) Require(VmocionUsbProtocol.Advances(previous, bench.Uptime), "physical V4 uptime advances");
                first = false; previous = bench.Uptime;
            }
            Require(!first, "live V4 fixture present");
            previous = 0; first = true;
            foreach (object raw in (System.Collections.IEnumerable)fixture["bench_v5_status_samples"])
            {
                VmocionUsbStatus bench = VmocionUsbProtocol.BenchStatus((string)raw);
                Require(bench.Firmware == "VMOCION_BENCH_100K_PAIR_V5" && bench.OutputOff && bench.FaultHealthy &&
                    !bench.FaultLatched && bench.Blue && !bench.Green, "decode physical V5 idle status after verified application flash");
                if (!first) Require(VmocionUsbProtocol.Advances(previous, bench.Uptime), "physical V5 uptime advances");
                first = false; previous = bench.Uptime;
            }
            Require(!first, "live V5 fixture present");
            foreach (object raw in (System.Collections.IEnumerable)fixture["nominal_2k_mock_status_samples"])
            {
                VmocionUsbStatus nominal = VmocionUsbProtocol.BenchStatus((string)raw);
                Require(nominal.Firmware == "VMOCION_NOMINAL_2K_AB_V1" && nominal.OutputOff && nominal.Blue &&
                    !nominal.Calibration && !nominal.Credentials, "decode nominal 2k idle status produced by mocked sketch; not physical evidence");
            }
            Console.WriteLine("USB codec verification passed; no output commands or analog measurements.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
