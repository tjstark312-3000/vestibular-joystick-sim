using System;
using System.Collections.Generic;
using System.Threading;

namespace VestibularJoystickSim
{
    // No serial device or Form is constructed by these tests.
    internal static class SerialOutputTests
    {
        private sealed class Clock : IOutputClock
        {
            private long now;
            public DateTime WallTime = DateTime.UtcNow;
            public Action ReadHook;
            public long Milliseconds
            {
                get
                {
                    long value = Interlocked.Read(ref now);
                    if (ReadHook != null) ReadHook();
                    return value;
                }
            }
            public void Set(long value) { Interlocked.Exchange(ref now, value); }
        }
        private sealed class Transport : IOutputTransport
        {
            public bool IsOpen { get; private set; }
            public readonly List<byte[]> Packets = new List<byte[]>();
            public Action<byte[]> BeforeWrite;
            public bool FailNext;
            public void Open() { IsOpen = true; }
            public void Close() { IsOpen = false; }
            public void Dispose() { Close(); }
            public void Write(byte[] packet)
            {
                if (!IsOpen) throw new InvalidOperationException("closed");
                if (FailNext) { FailNext = false; throw new InvalidOperationException("injected write failure"); }
                if (BeforeWrite != null) BeforeWrite(packet);
                Packets.Add((byte[])packet.Clone());
            }
        }

        private static readonly int[] Motion = new int[] { 28, 28, 228, 228 };
        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Serial sender test: " + message); }
        private static bool IsNeutral(byte[] p)
        { return p.Length == 9 && p[2] == 10 && p[3] == 128 && p[4] == 128 && p[5] == 128 && p[6] == 128; }
        private static bool IsMotion(byte[] p) { return p.Length == 9 && p[2] == 10 && !IsNeutral(p); }
        private static void OffTail(Transport t)
        {
            Require(t.Packets.Count >= 2 && IsNeutral(t.Packets[t.Packets.Count - 2]), "fault/disarm must send neutral first");
            byte[] reset = t.Packets[t.Packets.Count - 1];
            Require(reset.Length == 5 && reset[2] == 1, "fault/disarm must reset the session");
        }
        private static void NoResume(SerialOutput sender, Transport t, Clock c)
        {
            int count = t.Packets.Count;
            c.Set(c.Milliseconds + 25); sender.Publish(Motion); sender.Pump();
            Require(!sender.Status.Armed && t.Packets.Count == count, "fresh snapshots must never rearm a stopped sender");
        }

        public static bool Run()
        {
            GoldenAndFreshness();
            UiStall();
            SenderGap();
            WallClockIndependence();
            WriteFailure();
            InputFailure();
            DisarmRace();
            PublishWaitDoesNotRenewFreshness();
            RearmClearsUiHistory();
            Console.WriteLine("PASS serial sender: freshness, UI stall, sender gap, monotonic time, write/input failure, disarm race, queued snapshot age, rearm UI history");
            return true;
        }

        private static void GoldenAndFreshness()
        {
            Clock c = new Clock(); Transport t = new Transport();
            using (SerialOutput sender = new SerialOutput(c, false))
            {
                sender.Connect(t); sender.Arm();
                Require(t.Packets.Count == 4 && IsNeutral(t.Packets[0]) && t.Packets[1][2] == 1 &&
                    t.Packets[2][2] == 2 && IsNeutral(t.Packets[3]), "neutral/reset/direct/neutral arm order");
                Require(BitConverter.ToString(SerialOutput.Frame(new int[] { 10, 28, 28, 228, 228 })) ==
                    "AA-05-0A-1C-1C-E4-E4-0A-55", "legacy golden packet");
                sender.Publish(Motion);
                c.Set(25); sender.Pump();
                Require(IsMotion(t.Packets[t.Packets.Count - 1]), "fresh command at 25 ms");
                c.Set(75); sender.Pump();
                c.Set(99); sender.Pump();
                Require(sender.Status.Armed, "snapshot valid before 100 ms");
                c.Set(100); sender.Pump();
                Require(!sender.Status.Armed && sender.Status.Error.IndexOf("Input updates stopped") >= 0,
                    "snapshot expires exactly at 100 ms");
                OffTail(t); NoResume(sender, t, c);
                sender.Arm();
                Require(sender.Status.Armed && IsNeutral(t.Packets[t.Packets.Count - 1]), "explicit rearm starts with zero");
            }
        }

        private static void UiStall()
        {
            Clock c = new Clock(); Transport t = new Transport();
            using (SerialOutput sender = new SerialOutput(c, false))
            {
                sender.Connect(t); sender.Arm();
                for (int now = 25; now <= 500; now += 25)
                { c.Set(now); sender.Publish(Motion); sender.Pump(); Require(sender.Status.Armed, "healthy stream"); }
                // UI is frozen for 350 ms; background sender continues running.
                for (int now = 525; now <= 850; now += 25) { c.Set(now); sender.Pump(); }
                Require(!sender.Status.Armed, "UI stall must end output without waiting for firmware timeout");
                for (int i = 0; i < t.Packets.Count; i++)
                    if (i >= 4 + 23) Require(!IsMotion(t.Packets[i]), "no expired cached motion during UI stall");
                OffTail(t); NoResume(sender, t, c);
            }
        }

        private static void SenderGap()
        {
            Clock c = new Clock(); Transport t = new Transport();
            using (SerialOutput sender = new SerialOutput(c, false))
            {
                sender.Connect(t); sender.Arm(); sender.Publish(Motion);
                c.Set(25); sender.Pump();
                c.Set(275); sender.Publish(Motion); // Fresh UI cannot repair the known 250 ms TX gap.
                sender.Pump();
                Require(!sender.Status.Armed && sender.Status.Error.IndexOf("Sender gap") >= 0, "250 ms sender gap");
                OffTail(t); NoResume(sender, t, c);
            }
        }

        private static void WallClockIndependence()
        {
            Clock c = new Clock(); Transport t = new Transport();
            using (SerialOutput sender = new SerialOutput(c, false))
            {
                sender.Connect(t); sender.Arm();
                for (int i = 1; i <= 20; i++)
                {
                    c.WallTime = (i % 2 == 0) ? DateTime.MinValue : DateTime.MaxValue;
                    c.Set(i * 25); sender.Publish(Motion); sender.Pump();
                    Require(sender.Status.Armed && IsMotion(t.Packets[t.Packets.Count - 1]), "wall-clock corrections cannot suppress sends");
                }
                Require(t.Packets.Count == 24, "exact monotonic 25 ms send cadence");
            }
        }

        private static void WriteFailure()
        {
            Clock c = new Clock(); Transport t = new Transport();
            using (SerialOutput sender = new SerialOutput(c, false))
            {
                sender.Connect(t); sender.Arm(); sender.Publish(Motion);
                t.FailNext = true; c.Set(25); sender.Pump();
                Require(!sender.Status.Armed && sender.Status.Error.IndexOf("injected write failure") >= 0, "write failure disarms");
                OffTail(t); NoResume(sender, t, c);
            }
        }

        private static void InputFailure()
        {
            Clock c = new Clock(); Transport t = new Transport();
            using (SerialOutput sender = new SerialOutput(c, false))
            {
                sender.Connect(t); sender.Arm(); sender.Publish(Motion); c.Set(25); sender.Pump();
                sender.InputFault("injected input failure");
                Require(!sender.Status.Armed, "input exception disarms"); OffTail(t); NoResume(sender, t, c);
            }
        }

        private static void DisarmRace()
        {
            Clock c = new Clock(); Transport t = new Transport();
            using (SerialOutput sender = new SerialOutput(c, false))
            using (ManualResetEvent entered = new ManualResetEvent(false))
            using (ManualResetEvent release = new ManualResetEvent(false))
            using (ManualResetEvent requested = new ManualResetEvent(false))
            {
                sender.Connect(t); sender.Arm(); sender.Publish(Motion); c.Set(25);
                t.BeforeWrite = delegate(byte[] p)
                {
                    if (IsMotion(p)) { entered.Set(); Require(release.WaitOne(2000), "release blocked write"); }
                };
                Exception backgroundError = null;
                Thread send = new Thread(delegate() { try { sender.Pump(); } catch (Exception ex) { backgroundError = ex; } });
                Thread disarm = new Thread(delegate() { try { requested.Set(); sender.Disarm(); } catch (Exception ex) { backgroundError = ex; } });
                send.Start(); Require(entered.WaitOne(2000), "sender entered transport");
                disarm.Start(); Require(requested.WaitOne(2000), "concurrent disarm requested");
                release.Set();
                Require(send.Join(2000) && disarm.Join(2000) && backgroundError == null, "serialized disarm completes");
                t.BeforeWrite = null;
                Require(!sender.Status.Armed, "disarm wins over in-flight send"); OffTail(t); NoResume(sender, t, c);
            }
        }

        private static void PublishWaitDoesNotRenewFreshness()
        {
            Clock c = new Clock(); Transport t = new Transport();
            using (SerialOutput sender = new SerialOutput(c, false))
            using (ManualResetEvent entered = new ManualResetEvent(false))
            using (ManualResetEvent release = new ManualResetEvent(false))
            using (ManualResetEvent captured = new ManualResetEvent(false))
            {
                sender.Connect(t); sender.Arm(); sender.Publish(Motion); c.Set(25);
                t.BeforeWrite = delegate(byte[] p)
                { if (IsMotion(p)) { entered.Set(); Require(release.WaitOne(3000), "release slow write"); } };
                Exception backgroundError = null;
                Thread send = new Thread(delegate() { try { sender.Pump(); } catch (Exception ex) { backgroundError = ex; } });
                Thread publish = new Thread(delegate()
                {
                    try
                    {
                        int id = Thread.CurrentThread.ManagedThreadId;
                        c.ReadHook = delegate() { if (Thread.CurrentThread.ManagedThreadId == id) captured.Set(); };
                        sender.Publish(Motion);
                    }
                    catch (Exception ex) { backgroundError = ex; }
                });
                send.Start(); Require(entered.WaitOne(2000), "slow sender entered transport");
                publish.Start();
                bool timestampBeforeWait = captured.WaitOne(2000);
                // The queued command is now 150 ms old, but the sender gap is
                // below 250 ms. Only its original age should reject it.
                c.Set(175); release.Set();
                Require(send.Join(2000) && publish.Join(2000) && backgroundError == null, "queued publish completes");
                c.ReadHook = null; t.BeforeWrite = null;
                Require(timestampBeforeWait, "publish timestamps before waiting for serial lock");
                sender.Pump();
                Require(!sender.Status.Armed && sender.Status.Error.IndexOf("Input updates stopped") >= 0,
                    "lock wait must not refresh an expired snapshot");
                OffTail(t);
            }
        }

        private static void RearmClearsUiHistory()
        {
            Clock c = new Clock(); Transport t = new Transport();
            UiCommandState state = new UiCommandState();
            using (SerialOutput sender = new SerialOutput(c, false))
            {
                sender.Connect(t); sender.Arm(); sender.Publish(Motion); c.Set(25); sender.Pump();
                // Model held input and an unfinished calibration when the UI
                // stalls. The user releases the control before explicit rearm.
                state.PRate = 0.785; state.QRate = -0.785; state.RRate = 0.785;
                state.CommandedP = 0.785; state.CommandedQ = -0.785; state.CommandedR = 0.785;
                state.CalibrationDirection = 1; state.CalibrationStep = 33; state.CalibrationStart = DateTime.UtcNow;
                for (int i = 0; i < 4; i++)
                { state.CurrentBytes[i] = Motion[i]; state.PacketValues[i] = Motion[i] - 128; state.DacValues[i] = 15; }
                c.Set(100); sender.Pump(); Require(!sender.Status.Armed, "stalled session ended");

                // The Arm handler uses this same reset before the wire handshake.
                state.Reset(); sender.Arm();
                Require(state.PRate == 0 && state.QRate == 0 && state.RRate == 0 &&
                    state.CommandedP == 0 && state.CommandedQ == 0 && state.CommandedR == 0,
                    "old ramp and commanded axes cannot leak into a new arm");
                Require(state.CalibrationDirection == 0 && state.CalibrationStep == 0 &&
                    state.CalibrationStart == DateTime.MinValue, "calibration cannot restart on rearm");
                for (int i = 0; i < 4; i++)
                    Require(state.CurrentBytes[i] == 128 && state.PacketValues[i] == 0 && state.DacValues[i] == 0,
                        "all channels start neutral after rearm");
                int start = t.Packets.Count;
                for (int now = 125; now <= 175; now += 25)
                { c.Set(now); sender.Publish(state.CurrentBytes); sender.Pump(); }
                for (int i = start; i < t.Packets.Count; i++) Require(IsNeutral(t.Packets[i]), "released control remains neutral after rearm");
            }
        }
    }
}
