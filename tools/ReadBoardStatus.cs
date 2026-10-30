using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading;

// Standalone, output-off diagnostic. It never constructs the GUI sender.
internal static class ReadBoardStatus
{
    private const string StatusPrefix = "VMOCION_DIRECTIONAL_BENCH_V4";
    private static Dictionary<string,string> Parse(string line, string prefix)
    {
        string[] tokens = line.Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2 || tokens[0] != prefix) throw new InvalidDataException("Unexpected firmware reply prefix.");
        Dictionary<string,string> fields = new Dictionary<string,string>(StringComparer.Ordinal);
        for (int i=1;i<tokens.Length;i++)
        {
            Match token = Regex.Match(tokens[i], @"^([a-z_0-9]+)=([^=\s]+)$");
            if (!token.Success || fields.ContainsKey(token.Groups[1].Value)) throw new InvalidDataException("Malformed/duplicate firmware field.");
            fields.Add(token.Groups[1].Value, token.Groups[2].Value);
        }
        return fields;
    }
    private static string Field(Dictionary<string,string> fields, string key)
    { string value; if (!fields.TryGetValue(key,out value)) throw new InvalidDataException("Missing firmware field: " + key); return value; }
    private static void RequireOff(Dictionary<string,string> fields)
    {
        string[] names = {"active","arm_pin","carrier_pin","led_test_active","prepared","baseline_done","baseline_confirmed","session_armed","target_written","axis_r","axis_p","axis_y","target_a","target_b","target_c","target_d","applied_a","applied_b","applied_c","applied_d"};
        foreach (string key in names) if (Field(fields,key)!="0") throw new InvalidDataException("Firmware does not report output off: " + key);
        foreach (string key in new string[] {"fault_n","fault_latched"})
            if (Field(fields,key)!="0" && Field(fields,key)!="1") throw new InvalidDataException("Invalid " + key);
    }
    private static string Exchange(Action<string> write, Func<string> read, string command, string prefix)
    {
        if (command!="INFO" && command!="STATUS") throw new InvalidOperationException("Only INFO and STATUS are permitted.");
        write(command + "\n");
        Stopwatch deadline = Stopwatch.StartNew();
        while(deadline.ElapsedMilliseconds < 3000)
        {
            string line;
            try { line=read(); } catch(TimeoutException) { continue; }
            if(line.Length>8192) throw new InvalidDataException("Oversized firmware reply.");
            if(line.StartsWith(prefix+" ",StringComparison.Ordinal)) return line.Trim();
        }
        throw new TimeoutException("No " + command + " reply; installed firmware may not support this diagnostic.");
    }
    private static string DeviceIdentity(string port)
    {
        if(!Regex.IsMatch(port,@"^COM[0-9]+$")) throw new ArgumentException("Use a COM port such as COM10.");
        string identity=null;
        using(ManagementObjectSearcher search = new ManagementObjectSearcher("SELECT Name, PNPDeviceID FROM Win32_PnPEntity"))
        using(ManagementObjectCollection found=search.Get())
        foreach(ManagementObject item in found)
        using(item)
        {
            string name=Convert.ToString(item["Name"],CultureInfo.InvariantCulture);
            string id=Convert.ToString(item["PNPDeviceID"],CultureInfo.InvariantCulture);
            if(!name.EndsWith("("+port+")",StringComparison.OrdinalIgnoreCase) ||
                !id.StartsWith(@"USB\VID_2886&PID_8045",StringComparison.OrdinalIgnoreCase)) continue;
            if(identity!=null) throw new InvalidOperationException("Multiple devices match; refusing ambiguous selection.");
            identity=id;
        }
        if(identity==null) throw new InvalidOperationException("Selected port must be a XIAO Sense application device (2886:8045).");
        return identity;
    }
    private static void Inspect(string port)
    {
        if(Process.GetProcessesByName("VestibularJoystickSim").Length!=0)
            throw new InvalidOperationException("Close VFORCE first. Keep the stopped board powered to preserve its recorded cause.");
        string identity=DeviceIdentity(port);
        string infoLine, statusLine;
        using(SerialPort connection = new SerialPort(port,115200,Parity.None,8,StopBits.One))
        {
            connection.DtrEnable=true; connection.RtsEnable=false;
            connection.ReadTimeout=200; connection.WriteTimeout=500;
            connection.Open(); Thread.Sleep(3200);
            // Bound reads even if a device continuously sends a malformed line.
            Func<string> read = delegate {
                System.Text.StringBuilder line=new System.Text.StringBuilder();
                Stopwatch budget=Stopwatch.StartNew();
                while(budget.ElapsedMilliseconds<500)
                {
                    int next=connection.ReadChar();
                    if(next=='\n') return line.ToString();
                    if(next<0 || next>127 || line.Length>=8192) throw new InvalidDataException("Invalid firmware text.");
                    line.Append((char)next);
                }
                throw new TimeoutException("Incomplete firmware line.");
            };
            infoLine=Exchange(connection.Write,read,"INFO","VMOCION_INFO");
            statusLine=Exchange(connection.Write,read,"STATUS",StatusPrefix);
        }
        Dictionary<string,string> info=Parse(infoLine,"VMOCION_INFO"), status=Parse(statusLine,StatusPrefix);
        if(Field(info,"firmware")!=StatusPrefix) throw new InvalidDataException("Firmware identity mismatch.");
        RequireOff(status);
        string directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"VFORCE","Logs");
        Directory.CreateDirectory(directory);
        string path=Path.Combine(directory,"board-status-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")+".txt");
        File.WriteAllLines(path,new string[] {
            "Read-only firmware diagnostic; no active commands sent. Reported state, not measured electrical output.",
            "Opening diagnostics changes serial transport and stops output. A power cycle clears the RAM stop history.",
            "utc="+DateTime.UtcNow.ToString("o"),"port="+port,"pnp="+identity,infoLine,statusLine
        });
        Console.WriteLine("Saved: " + path);
        Console.WriteLine("Installed revision: " + Field(info,"build_revision"));
        Console.WriteLine("First recorded stop: " + Field(status,"last_stop_reason"));
        Console.WriteLine("Uptime ms: " + Field(status,"uptime_ms"));
        Console.WriteLine("Fault input: " + Field(status,"fault_n") + "; latched: " + Field(status,"fault_latched"));
    }
    private static bool SelfTest()
    {
        string example=StatusPrefix+" active=0 arm_pin=0 carrier_pin=0 led_test_active=0 prepared=0 baseline_done=0 baseline_confirmed=0 session_armed=0 target_written=0 axis_r=0 axis_p=0 axis_y=0 target_a=0 target_b=0 target_c=0 target_d=0 applied_a=0 applied_b=0 applied_c=0 applied_d=0 fault_n=1 fault_latched=0 last_stop_reason=HEARTBEAT_EXPIRED";
        RequireOff(Parse(example,StatusPrefix));
        if(Field(Parse(example,StatusPrefix),"last_stop_reason")!="HEARTBEAT_EXPIRED") return false;
        foreach(string bad in new string[] {example.Replace("active=0","active=1"),example.Replace("target_a=0","target_a=1"),example.Replace("fault_n=1","fault_n=bad"),example+" active=0",example+" invalid",example.Replace(StatusPrefix,"OTHER")})
        { bool rejected=false; try { RequireOff(Parse(bad,StatusPrefix)); } catch(InvalidDataException) {rejected=true;} if(!rejected) return false; }
        List<string> sent=new List<string>();
        foreach(string command in new string[] {"ARM","RESET","PREPARE","AXES 0 1 0"})
        {
            bool rejected=false;
            try { Exchange(sent.Add,delegate {return "unused x=1";},command,"unused"); } catch(InvalidOperationException) {rejected=true;}
            if(!rejected || sent.Count!=0) return false;
        }
        if(Exchange(sent.Add,delegate{return "VMOCION_INFO firmware="+StatusPrefix;},"INFO","VMOCION_INFO")==null) return false;
        if(Exchange(sent.Add,delegate{return example;},"STATUS",StatusPrefix)!=example) return false;
        return sent.Count==2 && sent[0]=="INFO\n" && sent[1]=="STATUS\n";
    }
    private static int Main(string[] args)
    {
        bool interactive=args.Length==0;
        try
        {
            if(args.Length==1 && args[0]=="--self-test")
            { if(!SelfTest()) throw new InvalidOperationException("Diagnostic self-test failed."); Console.WriteLine("PASS diagnostic command whitelist, fake exchange, output-off validation and preserved cutoff reason. No serial port opened."); return 0; }
            Console.WriteLine("READ BOARD STATUS ONLY - close VFORCE; keep the stopped board powered.\nThis changes serial transport and stops output. No reset, arm, motion or flash commands are sent.");
            string port;
            if(interactive) { Console.Write("Board application COM port (example COM10): "); port=Console.ReadLine(); }
            else if(args.Length==2 && args[0]=="--port") port=args[1];
            else throw new ArgumentException("Usage: ReadBoardStatus.exe --port COM10");
            Inspect((port ?? "").Trim().ToUpperInvariant()); return 0;
        }
        catch(Exception ex) {Console.Error.WriteLine("Diagnostic failed: " + ex.Message); return 1;}
        finally {if(interactive) {Console.WriteLine("Press Enter to close."); Console.ReadLine();}}
    }
}
