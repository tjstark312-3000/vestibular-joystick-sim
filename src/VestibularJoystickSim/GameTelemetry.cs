using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

namespace VestibularJoystickSim
{
    internal struct MotionSnapshot
    {
        private static readonly long FreshWindowTicks = (long)(Stopwatch.Frequency * 0.9);
        private long receivedTicks;

        public bool Connected;
        public DateTime TimestampUtc;
        public string Source;
        public double P;
        public double Q;
        public double R;
        public double RollDeg;
        public double PitchDeg;
        public double YawDeg;

        public bool IsFresh
        {
            get
            {
                long age = Stopwatch.GetTimestamp() - receivedTicks;
                return Connected && age >= 0 && age <= FreshWindowTicks;
            }
        }

        public static MotionSnapshot Fresh(string source, double p, double q, double r, double rollDeg, double pitchDeg, double yawDeg)
        {
            MotionSnapshot snapshot = new MotionSnapshot();
            snapshot.Connected = true;
            snapshot.TimestampUtc = DateTime.UtcNow;
            snapshot.receivedTicks = Stopwatch.GetTimestamp();
            snapshot.Source = source;
            snapshot.P = Sanitize(p);
            snapshot.Q = Sanitize(q);
            snapshot.R = Sanitize(r);
            snapshot.RollDeg = Sanitize(rollDeg);
            snapshot.PitchDeg = Sanitize(pitchDeg);
            snapshot.YawDeg = Sanitize(yawDeg);
            return snapshot;
        }

        public static MotionSnapshot Empty(string source)
        {
            MotionSnapshot snapshot = new MotionSnapshot();
            snapshot.Connected = false;
            snapshot.TimestampUtc = DateTime.MinValue;
            snapshot.Source = source;
            return snapshot;
        }

        public static bool SelfTest()
        {
            MotionSnapshot sample = Fresh("test", 0.1, 0.2, 0.3, 0, 0, 0);
            sample.TimestampUtc = DateTime.UtcNow.AddYears(1);
            if (!sample.IsFresh) return false; // Wall-clock changes do not age samples.
            sample.receivedTicks = Stopwatch.GetTimestamp() - FreshWindowTicks - Stopwatch.Frequency;
            if (sample.IsFresh) return false;
            sample.receivedTicks = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
            return !sample.IsFresh && !Empty("test").IsFresh;
        }

        private static double Sanitize(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return 0.0;
            }

            return value;
        }
    }

    internal sealed class ForzaUdpTelemetryInput : IDisposable
    {
        private const int PrimaryPort = 5300;
        private const int SecondaryPort = 5607;
        private const int MinimumSledPacketBytes = 232;
        private const int MaxPacketsPerPoll = 64;
        private readonly int primaryPort;
        private readonly int secondaryPort;
        private readonly List<UdpClient> clients = new List<UdpClient>();
        private MotionSnapshot latest = MotionSnapshot.Empty("Forza waiting on localhost UDP 5300/5607");
        private bool started;
        private string status = "Forza waiting on localhost UDP 5300/5607";
        private long lastPollTicks;

        public ForzaUdpTelemetryInput() : this(PrimaryPort, SecondaryPort) { }

        private ForzaUdpTelemetryInput(int primary, int secondary)
        {
            primaryPort = primary;
            secondaryPort = secondary;
        }

        public MotionSnapshot Poll()
        {
            long now = Stopwatch.GetTimestamp();
            if (lastPollTicks != 0 && now - lastPollTicks > Stopwatch.Frequency / 10)
            {
                // UDP receive times do not establish when queued packets were sent.
                // Discard the socket queues after a control-loop interruption.
                ResetStatus();
            }
            lastPollTicks = now;
            EnsureStarted();
            long deadline = now + Math.Max(1, Stopwatch.Frequency / 500); // 2 ms total.
            int packetBudget = MaxPacketsPerPoll;
            bool backlog = false;
            for (int i = 0; i < clients.Count; i++)
            {
                UdpClient client = clients[i];
                IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                while (true)
                {
                    byte[] data;
                    try
                    {
                        if (client.Available <= 0)
                        {
                            break;
                        }
                        if (packetBudget <= 0 || Stopwatch.GetTimestamp() >= deadline)
                        {
                            backlog = true;
                            break;
                        }
                        packetBudget--;
                        data = client.Receive(ref remote);
                    }
                    catch (SocketException)
                    {
                        status = "Forza UDP receive error";
                        latest = MotionSnapshot.Empty(status);
                        break;
                    }

                    if (!IPAddress.IsLoopback(remote.Address))
                    {
                        continue;
                    }

                    MotionSnapshot parsed;
                    if (TryParse(data, out parsed))
                    {
                        latest = parsed;
                        status = parsed.Source;
                    }
                    else if (string.Equals(parsed.Source, "Forza race not active", StringComparison.Ordinal))
                    {
                        // A valid Forza packet explicitly says the race stopped. Clear
                        // live motion immediately instead of waiting for freshness expiry.
                        latest = parsed;
                        status = parsed.Source;
                    }
                }
            }
            if (backlog)
            {
                ResetStatus();
                status = "Forza backlog discarded; waiting for fresh telemetry";
                latest = MotionSnapshot.Empty(status);
            }
            return latest.IsFresh ? latest : MotionSnapshot.Empty(status);
        }

        public static bool SelfTest()
        {
            byte[] packet = new byte[MinimumSledPacketBytes];
            WriteInt32(packet, 0, 1);
            WriteSingle(packet, 44, 0.2f);
            WriteSingle(packet, 48, 0.3f);
            WriteSingle(packet, 52, 0.1f);
            WriteSingle(packet, 56, 0.4f);
            WriteSingle(packet, 60, -0.2f);
            WriteSingle(packet, 64, 0.1f);

            MotionSnapshot motion;
            if (!TryParse(packet, out motion) ||
                Math.Abs(motion.P - 0.1) >= 0.0001 ||
                Math.Abs(motion.Q - 0.2) >= 0.0001 ||
                Math.Abs(motion.R - 0.3) >= 0.0001 ||
                Math.Abs(motion.RollDeg - (0.1 * 180.0 / Math.PI)) >= 0.0001 ||
                Math.Abs(motion.PitchDeg - (-0.2 * 180.0 / Math.PI)) >= 0.0001)
            {
                return false;
            }

            if (!MotionSnapshot.SelfTest()) return false;
            if (TryParse(null, out motion) || TryParse(new byte[231], out motion)) return false;
            WriteSingle(packet, 44, float.NaN);
            if (TryParse(packet, out motion)) return false;
            WriteSingle(packet, 44, float.PositiveInfinity);
            if (TryParse(packet, out motion)) return false;
            WriteSingle(packet, 44, 101.0f);
            if (TryParse(packet, out motion)) return false;
            WriteSingle(packet, 44, 0.2f);
            WriteInt32(packet, 0, 2);
            if (TryParse(packet, out motion)) return false;
            WriteInt32(packet, 0, 0);
            MotionSnapshot stopped;
            return !TryParse(packet, out stopped) && stopped.Source == "Forza race not active";
        }

        public void ResetStatus()
        {
            CloseClients();
            started = false;
            lastPollTicks = 0;
            status = "Forza waiting on localhost UDP 5300/5607";
            latest = MotionSnapshot.Empty(status);
        }

        private void EnsureStarted()
        {
            if (started)
            {
                return;
            }

            started = true;
            TryBind(primaryPort);
            TryBind(secondaryPort);

            if (clients.Count == 0)
            {
                status = "Forza UDP bind failed";
            }
        }

        private void TryBind(int port)
        {
            UdpClient client = null;
            try
            {
                client = new UdpClient();
                client.ExclusiveAddressUse = true;
                client.Client.Bind(new IPEndPoint(IPAddress.Loopback, port));
                clients.Add(client);
                status = "Forza waiting on localhost UDP " + port.ToString();
            }
            catch
            {
                if (client != null) client.Close();
                // Another telemetry app may own the port; keep any port that did bind.
            }
        }

        private static bool TryParse(byte[] data, out MotionSnapshot motion)
        {
            motion = MotionSnapshot.Empty("Forza invalid packet");
            if (data == null || data.Length < MinimumSledPacketBytes)
            {
                return false;
            }

            int isRaceOn = ReadInt32(data, 0);
            if (isRaceOn == 0)
            {
                motion = MotionSnapshot.Empty("Forza race not active");
                return false;
            }

            if (isRaceOn != 1)
            {
                return false;
            }

            float angularX = ReadSingle(data, 44);
            float angularY = ReadSingle(data, 48);
            float angularZ = ReadSingle(data, 52);
            float yaw = ReadSingle(data, 56);
            float pitch = ReadSingle(data, 60);
            float roll = ReadSingle(data, 64);

            if (!IsFiniteAndSane(angularX) || !IsFiniteAndSane(angularY) || !IsFiniteAndSane(angularZ) ||
                !IsFiniteAndSane(yaw) || !IsFiniteAndSane(pitch) || !IsFiniteAndSane(roll))
            {
                return false;
            }

            motion = MotionSnapshot.Fresh(
                "Forza UDP physics",
                angularZ,
                angularX,
                angularY,
                roll * 180.0 / Math.PI,
                pitch * 180.0 / Math.PI,
                yaw * 180.0 / Math.PI);
            return true;
        }

        private static bool IsFiniteAndSane(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && Math.Abs(value) <= 100.0f;
        }

        private static int ReadInt32(byte[] data, int offset)
        {
            return BitConverter.ToInt32(data, offset);
        }

        private static float ReadSingle(byte[] data, int offset)
        {
            if (offset + 4 > data.Length)
            {
                return 0.0f;
            }

            return BitConverter.ToSingle(data, offset);
        }

        private static void WriteSingle(byte[] data, int offset, float value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            Array.Copy(bytes, 0, data, offset, bytes.Length);
        }

        private static void WriteInt32(byte[] data, int offset, int value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            Array.Copy(bytes, 0, data, offset, bytes.Length);
        }

        private void CloseClients()
        {
            for (int i = 0; i < clients.Count; i++)
            {
                clients[i].Close();
            }

            clients.Clear();
        }

        public void Dispose()
        {
            CloseClients();
            started = false;
            latest = MotionSnapshot.Empty("Forza UDP closed");
        }

        public static bool LoopbackSelfTest()
        {
            // Ephemeral ports keep this test independent of installed games/apps.
            using (ForzaUdpTelemetryInput input = new ForzaUdpTelemetryInput(0, 0))
            using (UdpClient sender = new UdpClient())
            {
                input.Poll();
                if (input.clients.Count != 2) return false;
                byte[] packet = new byte[MinimumSledPacketBytes];
                WriteInt32(packet, 0, 1);
                WriteSingle(packet, 52, 0.25f);
                if (!WaitForLoopbackPacket(input, sender, packet, true)) return false;
                WriteInt32(packet, 0, 0);
                if (!WaitForLoopbackPacket(input, sender, packet, false)) return false;
                WriteInt32(packet, 0, 1);
                if (!QueueLoopbackPacket(input, sender, packet)) return false;
                input.ResetStatus();
                if (input.Poll().IsFresh || input.clients.Count != 2) return false;
                // A valid datagram was observed in the old socket's queue before
                // reset, so this assertion verifies queue disposal, not delivery luck.
                // A stalled poll must also discard buffered motion.
                if (!QueueLoopbackPacket(input, sender, packet)) return false;
                input.lastPollTicks = Stopwatch.GetTimestamp() - Stopwatch.Frequency;
                if (input.Poll().IsFresh || input.clients.Count != 2) return false;
                // Rebinding after the safety discard must still accept new traffic.
                return WaitForLoopbackPacket(input, sender, packet, true);
            }
        }

        private static bool WaitForLoopbackPacket(ForzaUdpTelemetryInput input, UdpClient sender,
            byte[] packet, bool expectActive)
        {
            long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 3;
            while (Stopwatch.GetTimestamp() < deadline)
            {
                // Poll may replace ephemeral sockets after a CI scheduling pause.
                // Resolve the endpoint each attempt, and send again after a reset.
                input.Poll();
                if (input.clients.Count != 2) return false;
                IPEndPoint target = (IPEndPoint)input.clients[0].Client.LocalEndPoint;
                sender.Send(packet, packet.Length, target);
                Thread.Sleep(1);
                MotionSnapshot sample = input.Poll();
                if (expectActive)
                {
                    if (sample.IsFresh && sample.Source == "Forza UDP physics" &&
                        Math.Abs(sample.P - 0.25) <= 0.0001) return true;
                }
                else if (!sample.IsFresh && sample.Source == "Forza race not active")
                {
                    return true;
                }
            }
            return false;
        }

        private static bool QueueLoopbackPacket(ForzaUdpTelemetryInput input, UdpClient sender, byte[] packet)
        {
            if (input.clients.Count != 2) return false;
            UdpClient receiver = input.clients[0];
            IPEndPoint target = (IPEndPoint)receiver.Client.LocalEndPoint;
            sender.Send(packet, packet.Length, target);
            long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
            while (receiver.Available == 0 && Stopwatch.GetTimestamp() < deadline)
                Thread.Sleep(1);
            return receiver.Available > 0;
        }
    }

    internal sealed class MsfsSimConnectInput : IDisposable
    {
        private const uint SimConnectObjectIdUser = 0;
        private const uint SimConnectUnused = 0xffffffff;
        private const uint DefinitionMotion = 1;
        private const uint RequestMotion = 1;
        private const uint SimConnectDatatypeFloat64 = 4;
        private const uint SimConnectPeriodSimFrame = 3;
        private const uint SimConnectDataRequestFlagDefault = 0;
        private const uint SimConnectRecvIdQuit = 3;
        private const uint SimConnectRecvIdException = 1;
        private const uint SimConnectRecvIdEvent = 4;
        private const uint SimConnectRecvIdSimobjectData = 8;
        private const uint EventPause = 10;
        private const uint EventSim = 11;
        private const uint EventPauseExtended = 12;
        private const int SimobjectDataOffset = 40;

        private readonly int windowMessage;
        private readonly DispatchProc dispatchProc;
        private IntPtr handle = IntPtr.Zero;
        private long lastConnectAttemptTicks;
        private MotionSnapshot latest = MotionSnapshot.Empty("MSFS waiting for SimConnect");
        private string status = "MSFS waiting for SimConnect";
        private bool definitionsRegistered;
        private bool disconnectRequested;
        private bool pauseKnown;
        private bool paused;
        private bool extendedPaused;
        private bool simRunning;

        public MsfsSimConnectInput(int msfsWindowMessage)
        {
            windowMessage = msfsWindowMessage;
            dispatchProc = Dispatch;
        }

        public static bool SelfTest()
        {
            MotionSnapshot motion = MapBodyRates(0.1, 0.2, 0.3);
            if (Math.Abs(motion.P - 0.3) >= 0.0001 ||
                Math.Abs(motion.Q - 0.1) >= 0.0001 ||
                Math.Abs(motion.R - 0.2) >= 0.0001) return false;
            if (MapBodyRates(double.NaN, 0, 0).IsFresh || MapBodyRates(101, 0, 0).IsFresh) return false;
            MsfsSimConnectInput input = new MsfsSimConnectInput(0x402);
            IntPtr data = Marshal.AllocHGlobal(64);
            try
            {
                byte[] empty = new byte[64];
                Marshal.Copy(empty, 0, data, empty.Length);
                Marshal.WriteInt32(data, 0, 64);
                Marshal.WriteInt32(data, 8, (int)SimConnectRecvIdSimobjectData);
                Marshal.WriteInt32(data, 12, (int)RequestMotion);
                Marshal.WriteInt32(data, 20, (int)DefinitionMotion);
                Marshal.WriteInt32(data, 28, 1);
                Marshal.WriteInt32(data, 32, 1);
                Marshal.WriteInt32(data, 36, 3);
                Marshal.WriteInt64(data, 40, BitConverter.DoubleToInt64Bits(0.1));
                Marshal.WriteInt64(data, 48, BitConverter.DoubleToInt64Bits(0.2));
                Marshal.WriteInt64(data, 56, BitConverter.DoubleToInt64Bits(0.3));
                input.Dispatch(data, 64, IntPtr.Zero);
                if (input.latest.IsFresh) return false; // Unknown simulator state is neutral.
                input.pauseKnown = true;
                input.simRunning = true;
                input.Dispatch(data, 64, IntPtr.Zero);
                if (!input.latest.IsFresh || Math.Abs(input.latest.P - 0.3) > 0.0001) return false;
                input.latest = MotionSnapshot.Empty("test");
                Marshal.WriteInt32(data, 12, 99);
                input.Dispatch(data, 64, IntPtr.Zero);
                if (input.latest.IsFresh) return false;
                Marshal.WriteInt32(data, 12, (int)RequestMotion);
                Marshal.WriteInt32(data, 36, 2);
                input.Dispatch(data, 64, IntPtr.Zero);
                if (input.latest.IsFresh) return false;
                Marshal.WriteInt32(data, 36, 3);
                input.Dispatch(data, 63, IntPtr.Zero);
                if (input.latest.IsFresh) return false;
                Marshal.WriteInt32(data, 0, 24);
                Marshal.WriteInt32(data, 8, (int)SimConnectRecvIdEvent);
                Marshal.WriteInt32(data, 16, (int)EventPauseExtended);
                Marshal.WriteInt32(data, 20, 4);
                input.Dispatch(data, 24, IntPtr.Zero);
                if (!input.extendedPaused || input.latest.IsFresh) return false;
                Marshal.WriteInt32(data, 16, (int)EventPause);
                Marshal.WriteInt32(data, 20, 0);
                input.Dispatch(data, 24, IntPtr.Zero);
                if (!input.extendedPaused) return false; // Legacy event cannot cancel Active Pause.
                Marshal.WriteInt32(data, 16, (int)EventPauseExtended);
                input.Dispatch(data, 24, IntPtr.Zero);
                if (input.extendedPaused) return false;
                Marshal.WriteInt32(data, 16, (int)EventSim);
                input.Dispatch(data, 24, IntPtr.Zero);
                if (input.simRunning || input.latest.IsFresh) return false;
                Marshal.WriteInt32(data, 0, 12);
                Marshal.WriteInt32(data, 8, (int)SimConnectRecvIdQuit);
                input.Dispatch(data, 12, IntPtr.Zero);
                return input.disconnectRequested;
            }
            finally { Marshal.FreeHGlobal(data); }
        }

        public MotionSnapshot Poll(IntPtr windowHandle)
        {
            EnsureConnected(windowHandle);
            return pauseKnown && simRunning && !paused && !extendedPaused && latest.IsFresh
                ? latest : MotionSnapshot.Empty(status);
        }

        public void HandleWindowMessage(int message)
        {
            if (message != windowMessage || handle == IntPtr.Zero)
            {
                return;
            }

            DispatchPending();
        }

        public void ResetStatus()
        {
            // Close the receive queue when selecting a feed so an earlier session
            // cannot be relabelled as fresh motion after the selection changes.
            Close();
            lastConnectAttemptTicks = 0;
            status = "MSFS waiting for SimConnect";
            latest = MotionSnapshot.Empty(status);
        }

        private void DispatchPending()
        {
            if (handle == IntPtr.Zero)
            {
                return;
            }

            try
            {
                int hr = SimConnect_CallDispatch(handle, dispatchProc, IntPtr.Zero);
                if (disconnectRequested)
                {
                    MarkDisconnected("MSFS waiting for simulator");
                }
                else if (hr < 0)
                {
                    MarkDisconnected("MSFS SimConnect disconnected");
                }
            }
            catch
            {
                MarkDisconnected("MSFS SimConnect disconnected");
            }
        }

        private void MarkDisconnected(string disconnectedStatus)
        {
            Close();
            status = disconnectedStatus;
            latest = MotionSnapshot.Empty(status);
        }

        private void EnsureConnected(IntPtr windowHandle)
        {
            if (handle != IntPtr.Zero || windowHandle == IntPtr.Zero)
            {
                return;
            }

            long now = Stopwatch.GetTimestamp();
            if (lastConnectAttemptTicks != 0 && now - lastConnectAttemptTicks < Stopwatch.Frequency * 2)
            {
                return;
            }

            lastConnectAttemptTicks = now;
            IntPtr simConnect;
            try
            {
                int hr = SimConnect_Open(out simConnect, "Vestibular Joystick Simulation", windowHandle, (uint)windowMessage, IntPtr.Zero, 0);
                if (hr != 0 || simConnect == IntPtr.Zero)
                {
                    status = "MSFS waiting for simulator";
                    return;
                }

                handle = simConnect;
                disconnectRequested = false;
                RegisterDefinitions();
                status = "MSFS SimConnect connected";
            }
            catch (DllNotFoundException)
            {
                MarkDisconnected("MSFS SimConnect.dll missing");
            }
            catch
            {
                MarkDisconnected("MSFS SimConnect failed");
            }
        }

        private void RegisterDefinitions()
        {
            if (definitionsRegistered || handle == IntPtr.Zero)
            {
                return;
            }

            AddDouble("ROTATION VELOCITY BODY X", "radians per second");
            AddDouble("ROTATION VELOCITY BODY Y", "radians per second");
            AddDouble("ROTATION VELOCITY BODY Z", "radians per second");

            Subscribe(EventPause, "Pause");
            Subscribe(EventPauseExtended, "Pause_EX1");
            Subscribe(EventSim, "Sim");

            int hr = SimConnect_RequestDataOnSimObject(
                handle,
                RequestMotion,
                DefinitionMotion,
                SimConnectObjectIdUser,
                SimConnectPeriodSimFrame,
                SimConnectDataRequestFlagDefault,
                0,
                0,
                0);
            if (hr != 0)
            {
                throw new InvalidOperationException("SimConnect request failed.");
            }

            definitionsRegistered = true;
        }

        private void Subscribe(uint eventId, string name)
        {
            if (SimConnect_SubscribeToSystemEvent(handle, eventId, name) != 0)
                throw new InvalidOperationException("SimConnect event subscription failed: " + name);
        }

        private void AddDouble(string name, string units)
        {
            int hr = SimConnect_AddToDataDefinition(handle, DefinitionMotion, name, units, SimConnectDatatypeFloat64, 0.0f, SimConnectUnused);
            if (hr != 0)
            {
                throw new InvalidOperationException("SimConnect definition failed: " + name);
            }
        }

        private void Dispatch(IntPtr data, uint cbData, IntPtr context)
        {
            if (data == IntPtr.Zero || cbData < 12)
            {
                return;
            }

            uint declaredSize = (uint)Marshal.ReadInt32(data, 0);
            if (declaredSize < 12 || declaredSize > cbData) return;

            uint receiveId = (uint)Marshal.ReadInt32(data, 8);
            if (receiveId == SimConnectRecvIdQuit)
            {
                // Close after SimConnect_CallDispatch returns; closing from inside its
                // callback risks re-entering the native library.
                disconnectRequested = true;
                latest = MotionSnapshot.Empty("MSFS simulator closed");
                return;
            }

            if (receiveId == SimConnectRecvIdException)
            {
                // Async definition/subscription failures must not leave cached
                // motion live. Reopen outside the native callback.
                latest = MotionSnapshot.Empty("MSFS SimConnect exception");
                disconnectRequested = true;
                return;
            }

            if (receiveId == SimConnectRecvIdEvent && declaredSize >= 24)
            {
                uint eventId = (uint)Marshal.ReadInt32(data, 16);
                uint value = (uint)Marshal.ReadInt32(data, 20);
                if (eventId == EventPause) { pauseKnown = true; paused = value != 0; }
                else if (eventId == EventPauseExtended) { pauseKnown = true; extendedPaused = value != 0; }
                else if (eventId == EventSim) simRunning = value == 1;
                else return;
                status = !simRunning ? "MSFS simulator not running" :
                    (paused || extendedPaused ? "MSFS paused" : "MSFS waiting for fresh physics");
                latest = MotionSnapshot.Empty(status);
                return;
            }

            if (receiveId != SimConnectRecvIdSimobjectData || declaredSize < SimobjectDataOffset + 24 ||
                (uint)Marshal.ReadInt32(data, 12) != RequestMotion ||
                (uint)Marshal.ReadInt32(data, 16) != SimConnectObjectIdUser ||
                (uint)Marshal.ReadInt32(data, 20) != DefinitionMotion ||
                Marshal.ReadInt32(data, 24) != 0 || Marshal.ReadInt32(data, 28) != 1 ||
                Marshal.ReadInt32(data, 32) != 1 || Marshal.ReadInt32(data, 36) != 3)
            {
                return;
            }

            if (!pauseKnown || !simRunning || paused || extendedPaused) return;

            double bodyX = ReadDouble(data, SimobjectDataOffset);
            double bodyY = ReadDouble(data, SimobjectDataOffset + 8);
            double bodyZ = ReadDouble(data, SimobjectDataOffset + 16);
            latest = MapBodyRates(bodyX, bodyY, bodyZ);
            status = latest.Source;
        }

        private static MotionSnapshot MapBodyRates(double bodyX, double bodyY, double bodyZ)
        {
            if (!IsFiniteAndSane(bodyX) || !IsFiniteAndSane(bodyY) || !IsFiniteAndSane(bodyZ))
                return MotionSnapshot.Empty("MSFS invalid physics");
            // Flight Simulator body axes: X=lateral/pitch, Y=vertical/yaw,
            // Z=longitudinal/roll. Convert them to the P/Q/R convention.
            return MotionSnapshot.Fresh("MSFS SimConnect physics", bodyZ, bodyX, bodyY, 0.0, 0.0, 0.0);
        }

        private static bool IsFiniteAndSane(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && Math.Abs(value) <= 100.0;
        }

        private static double ReadDouble(IntPtr pointer, int offset)
        {
            return BitConverter.Int64BitsToDouble(Marshal.ReadInt64(pointer, offset));
        }

        private void Close()
        {
            if (handle != IntPtr.Zero)
            {
                try
                {
                    SimConnect_Close(handle);
                }
                catch
                {
                }
            }

            handle = IntPtr.Zero;
            definitionsRegistered = false;
            disconnectRequested = false;
            pauseKnown = false;
            paused = false;
            extendedPaused = false;
            simRunning = false;
            latest = MotionSnapshot.Empty("MSFS disconnected");
        }

        public void Dispose()
        {
            Close();
        }

        private delegate void DispatchProc(IntPtr data, uint cbData, IntPtr context);

        [DllImport("SimConnect.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        private static extern int SimConnect_Open(out IntPtr phSimConnect, string name, IntPtr hWnd, uint userEventWin32, IntPtr hEventHandle, uint configIndex);

        [DllImport("SimConnect.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int SimConnect_Close(IntPtr hSimConnect);

        [DllImport("SimConnect.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        private static extern int SimConnect_AddToDataDefinition(IntPtr hSimConnect, uint defineId, string datumName, string unitsName, uint datumType, float epsilon, uint datumId);

        [DllImport("SimConnect.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int SimConnect_RequestDataOnSimObject(IntPtr hSimConnect, uint requestId, uint defineId, uint objectId, uint period, uint flags, uint origin, uint interval, uint limit);

        [DllImport("SimConnect.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int SimConnect_CallDispatch(IntPtr hSimConnect, DispatchProc dispatchProc, IntPtr context);

        [DllImport("SimConnect.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        private static extern int SimConnect_SubscribeToSystemEvent(IntPtr hSimConnect, uint eventId, string name);
    }

}
