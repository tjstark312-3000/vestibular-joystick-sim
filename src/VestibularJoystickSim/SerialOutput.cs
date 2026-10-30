using System;
using System.Diagnostics;
#if !SERIAL_OUTPUT_TEST
using System.IO.Ports;
#endif
using System.Threading;

namespace VestibularJoystickSim
{
    // UI-side ramp/calibration history belongs to the previous explicit arm.
    // Keeping this reset independent of Forms makes the rearm boundary testable.
    internal sealed class UiCommandState
    {
        public double PRate, QRate, RRate, CommandedP, CommandedQ, CommandedR;
        public int CalibrationDirection, CalibrationStep;
        public DateTime CalibrationStart;
        public readonly double[] PacketValues = new double[4];
        public readonly int[] CurrentBytes = new int[] { 128, 128, 128, 128 };
        public readonly double[] DacValues = new double[4];
        public void Reset()
        {
            PRate = QRate = RRate = CommandedP = CommandedQ = CommandedR = 0.0;
            CalibrationDirection = CalibrationStep = 0;
            CalibrationStart = DateTime.MinValue;
            for (int i = 0; i < 4; i++)
            { PacketValues[i] = DacValues[i] = 0.0; CurrentBytes[i] = 128; }
        }
    }

    internal interface IOutputClock { long Milliseconds { get; } }
    internal sealed class OutputClock : IOutputClock
    {
        private readonly Stopwatch watch = Stopwatch.StartNew();
        public long Milliseconds { get { return watch.ElapsedMilliseconds; } }
    }

    internal interface IOutputTransport : IDisposable
    {
        bool IsOpen { get; }
        void Open();
        void Write(byte[] packet);
        void SettleAfterReset();
        void Close();
    }

#if !SERIAL_OUTPUT_TEST
    internal sealed class SerialOutputTransport : IOutputTransport
    {
        private readonly SerialPort port;
        public SerialOutputTransport(SerialPort port) { this.port = port; }
        public bool IsOpen { get { return port.IsOpen; } }
        public void Open() { port.Open(); }
        public void Write(byte[] packet) { port.Write(packet, 0, packet.Length); }
        public void SettleAfterReset()
        {
            // NRF Reset deliberately drains its receive queue. A host Write
            // completing does not mean the board has processed those bytes.
            // Finish host transmission, then let Reset finish before sending
            // the fresh Arm/neutral sequence. This wait occurs while disarmed.
            Stopwatch pending = Stopwatch.StartNew();
            while (port.BytesToWrite > 0 && pending.ElapsedMilliseconds < 100)
                Thread.Sleep(1);
            if (port.BytesToWrite > 0)
                throw new TimeoutException("Serial reset did not finish transmitting.");
            Thread.Sleep(100);
        }
        public void Close() { port.Close(); }
        public void Dispose() { port.Dispose(); }
    }
#endif

    internal sealed class OutputStatus
    {
        public readonly bool Connected, Armed;
        public readonly int PacketCount;
        public readonly string LastPacket, Error;
        public long SnapshotAgeMs, LastSendAgeMs, MaxWriteMs, MaxSendGapMs;
        public OutputStatus(bool connected, bool armed, int packets, string last, string error)
        { Connected = connected; Armed = armed; PacketCount = packets; LastPacket = last; Error = error; }
    }

    // One lock owns the port, lifecycle, snapshots and all writes. A dedicated
    // worker cannot reenter itself, and never uses the UI thread or wall clock.
    internal sealed class SerialOutput : IDisposable
    {
        internal const int SendIntervalMs = 25;
        internal const int SnapshotLifetimeMs = 100;
        internal const int SenderGapMs = 250;
        private readonly object gate = new object();
        private readonly IOutputClock clock;
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly Thread worker;
        private volatile bool stopping;
        private bool disposed, armed;
        private IOutputTransport transport;
        private int[] values = new int[] { 128, 128, 128, 128 };
        private long publishedAt, lastSendAt;
        private int packetCount;
        private long maxWriteMs, maxSendGapMs;
        private string lastPacket = "-", error;

        public SerialOutput() : this(new OutputClock(), true) { }
        internal SerialOutput(IOutputClock clock, bool runWorker)
        {
            this.clock = clock;
            if (runWorker)
            {
                worker = new Thread(Run);
                worker.IsBackground = true;
                worker.Name = "VMocion serial sender";
                worker.Start();
            }
        }

        public OutputStatus Status
        {
            get { lock (gate) {
                long now = clock.Milliseconds;
                return new OutputStatus(Connected, armed, packetCount, lastPacket, error) {
                    SnapshotAgeMs = now - publishedAt, LastSendAgeMs = now - lastSendAt,
                    MaxWriteMs = maxWriteMs, MaxSendGapMs = maxSendGapMs };
            } }
        }
        private bool Connected { get { return transport != null && transport.IsOpen; } }
        private void CheckDisposed() { if (disposed) throw new ObjectDisposedException("SerialOutput"); }
        private void ClearSnapshot(long now)
        { values = new int[] { 128, 128, 128, 128 }; publishedAt = now; }

        public void Connect(IOutputTransport next)
        {
            lock (gate)
            {
                CheckDisposed();
                DisconnectLocked();
                transport = next;
                try { transport.Open(); }
                catch { CloseLocked(); throw; }
                packetCount = 0; maxWriteMs = maxSendGapMs = 0; lastPacket = "-"; error = null;
                ClearSnapshot(clock.Milliseconds);
            }
        }

        public void Publish(int[] command)
        {
            // The command was computed before this call. Waiting behind a port
            // write must never relabel an old UI snapshot with a new timestamp.
            long computedAt = clock.Milliseconds;
            if (command == null || command.Length != 4) throw new ArgumentException("Four channels required.");
            int[] copy = (int[])command.Clone();
            foreach (int value in copy)
                if (value < 28 || value > 228) throw new ArgumentOutOfRangeException("command");
            lock (gate)
            {
                CheckDisposed();
                values = copy;
                publishedAt = computedAt;
                wake.Set();
            }
        }

        public void Arm()
        {
            lock (gate)
            {
                CheckDisposed();
                if (!Connected) throw new InvalidOperationException("Connect a serial device first.");
                if (armed) return;
                ClearSnapshot(clock.Milliseconds);
                try
                {
                    // Establish output-off and reset any blocked/old session
                    // before the unchanged Direct + exact-neutral handshake.
                    WriteLocked(NeutralFrame());
                    WriteLocked(Frame(new int[] { 1 }));
                    transport.SettleAfterReset();
                    WriteLocked(Frame(new int[] { 2 }));
                    WriteLocked(NeutralFrame());
                    ClearSnapshot(clock.Milliseconds);
                    lastSendAt = clock.Milliseconds;
                    error = null;
                    armed = true;
                    wake.Set();
                }
                catch (Exception ex) { FaultLocked("Arm failed: " + ex.Message); throw; }
            }
        }

        public void Disarm()
        {
            lock (gate)
            {
                CheckDisposed();
                DisarmLocked(null);
            }
        }

        public void InputFault(string message)
        {
            lock (gate)
            {
                if (disposed) return;
                if (armed) FaultLocked("Input error - disarmed: " + message);
                else error = "Input error - disarmed: " + message;
            }
        }

        public void Neutral()
        {
            lock (gate)
            {
                CheckDisposed();
                long now = clock.Milliseconds;
                ClearSnapshot(now);
                if (!Connected) return;
                if (armed && now - lastSendAt >= SenderGapMs)
                { FaultLocked("Sender gap - disarmed; press Arm to start a new session."); return; }
                try { WriteLocked(NeutralFrame()); lastSendAt = clock.Milliseconds; }
                catch (Exception ex) { FaultLocked("Neutral write failed: " + ex.Message); throw; }
            }
        }

        // Also used by deterministic tests with a fake monotonic clock.
        internal void Pump()
        {
            lock (gate)
            {
                if (disposed || !armed) return;
                try
                {
                    long now = clock.Milliseconds;
                    maxSendGapMs = Math.Max(maxSendGapMs, now - lastSendAt);
                    if (!Connected) { FaultLocked("Serial disconnected - disarmed."); return; }
                    if (now - lastSendAt >= SenderGapMs)
                    { FaultLocked("Sender gap - disarmed; press Arm to start a new session."); return; }
                    if (now - publishedAt >= SnapshotLifetimeMs)
                    { FaultLocked("Input updates stopped - disarmed; press Arm to start a new session."); return; }
                    if (now - lastSendAt < SendIntervalMs) return;
                    WriteLocked(Frame(new int[] { 10, values[0], values[1], values[2], values[3] }));
                    long completed = clock.Milliseconds;
                    if (completed - lastSendAt >= SenderGapMs)
                    { FaultLocked("Sender gap - disarmed; press Arm to start a new session."); return; }
                    lastSendAt = completed;
                }
                catch (Exception ex) { FaultLocked("Serial error - disarmed: " + ex.Message); }
            }
        }

        private void Run()
        {
            while (!stopping)
            {
                Pump();
                int delay = SendIntervalMs;
                lock (gate)
                {
                    if (armed)
                    {
                        long now = clock.Milliseconds;
                        delay = (int)Math.Max(1, Math.Min(delay, Math.Min(
                            lastSendAt + SendIntervalMs - now, publishedAt + SnapshotLifetimeMs - now)));
                    }
                }
                if (!stopping) wake.WaitOne(delay);
            }
        }

        private void WriteLocked(byte[] packet)
        {
            long began = clock.Milliseconds;
            transport.Write(packet);
            maxWriteMs = Math.Max(maxWriteMs, clock.Milliseconds - began);
            packetCount++;
            lastPacket = BitConverter.ToString(packet).Replace('-', ' ');
        }
        private void DisarmLocked(string reason)
        {
            armed = false;
            ClearSnapshot(clock.Milliseconds);
            if (reason != null) error = reason;
            if (!Connected) return;
            try { WriteLocked(NeutralFrame()); WriteLocked(Frame(new int[] { 1 })); }
            catch (Exception ex)
            {
                error = (reason == null ? "Disarm write failed: " : reason + " / ") + ex.Message;
                CloseLocked();
            }
        }
        private void FaultLocked(string reason)
        {
            RuntimeDiagnostics.Record("OUTPUT_FAULT " + reason + " snapshotAgeMs=" + (clock.Milliseconds - publishedAt) +
                " sendAgeMs=" + (clock.Milliseconds - lastSendAt) + " packets=" + packetCount + " lastTX=" + lastPacket);
            DisarmLocked(reason);
        }
        private void CloseLocked()
        {
            armed = false;
            IOutputTransport old = transport;
            transport = null;
            if (old == null) return;
            try { old.Close(); } catch { }
            try { old.Dispose(); } catch { }
        }
        private void DisconnectLocked() { DisarmLocked(null); CloseLocked(); }
        public void Disconnect() { lock (gate) { if (!disposed) DisconnectLocked(); } }
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                DisconnectLocked();
                disposed = true; stopping = true;
            }
            wake.Set();
            if (worker == null || worker.Join(1000)) wake.Dispose();
        }

        private static byte[] NeutralFrame() { return Frame(new int[] { 10, 128, 128, 128, 128 }); }
        internal static byte[] Frame(int[] payload)
        {
            byte[] packet = new byte[payload.Length + 4];
            packet[0] = 0xAA; packet[1] = (byte)payload.Length;
            int sum = 0;
            for (int i = 0; i < payload.Length; i++) { packet[i + 2] = (byte)payload[i]; sum += payload[i]; }
            packet[packet.Length - 2] = (byte)sum; packet[packet.Length - 1] = 0x55;
            return packet;
        }
    }
}
