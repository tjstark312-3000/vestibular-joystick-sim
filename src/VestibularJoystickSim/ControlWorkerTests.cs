using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace VestibularJoystickSim
{
    internal static class ControlWorkerTests
    {
        private static readonly int[] Motion = new int[] { 128, 148, 128, 108 };
        private sealed class Clock : IOutputClock
        {
            internal long Now;
            public long Milliseconds { get { return Now; } }
        }
        private sealed class Transport : IOutputTransport
        {
            private readonly object gate = new object();
            private volatile bool open;
            internal volatile int MotionCount;
            public bool IsOpen { get { return open; } }
            public void Open() { open = true; }
            public void Close() { open = false; }
            public void Dispose() { Close(); }
            public void SettleAfterReset() { }
            public void Write(byte[] packet)
            {
                if (!open) throw new InvalidOperationException("closed test transport");
                lock (gate)
                    if (packet.Length == 9 && packet[2] == 10 &&
                        (packet[3] != 128 || packet[4] != 128 || packet[5] != 128 || packet[6] != 128)) MotionCount++;
            }
        }
        private static void Require(bool value, string reason)
        { if (!value) throw new InvalidOperationException(reason); }
        internal static bool Run()
        {
            UiThreadCanWait();
            BlockedAcquisitionStops();
            AcquisitionFailureStops();
            StaleCompletionAndLongUptime();
            return true;
        }
        private static void UiThreadCanWait()
        {
            OutputClock clock = new OutputClock(); Transport port = new Transport();
            using (SerialOutput sender = new SerialOutput(clock, true))
            {
                sender.Connect(port); sender.Arm();
                using (ControlWorker input = new ControlWorker(delegate
                { sender.PublishAt(Motion, clock.Milliseconds); }, delegate(Exception ex) { sender.InputFault(ex.Message); }, true))
                {
                    // No Application.DoEvents or UI callback is available during
                    // this wait. Native acquisition and serial output still run.
                    Thread.Sleep(650);
                    Require(input.IsRunning && sender.Status.Armed && port.MotionCount >= 10,
                        "live background input must continue through a blocked UI thread");
                }
                sender.Disarm();
            }
        }
        private static void BlockedAcquisitionStops()
        {
            OutputClock clock = new OutputClock(); Transport port = new Transport();
            using (ManualResetEvent entered = new ManualResetEvent(false))
            using (ManualResetEvent release = new ManualResetEvent(false))
            using (SerialOutput sender = new SerialOutput(clock, true))
            {
                sender.Connect(port); sender.Arm();
                int calls = 0;
                using (ControlWorker input = new ControlWorker(delegate
                {
                    long acquiredAt = clock.Milliseconds;
                    if (Interlocked.Increment(ref calls) > 2) { entered.Set(); release.WaitOne(); }
                    sender.PublishAt(Motion, acquiredAt);
                }, delegate(Exception ex) { sender.InputFault(ex.Message); }, true))
                {
                    try
                    {
                        Require(entered.WaitOne(1000), "acquisition did not enter injected stall");
                        Thread.Sleep(230);
                        Require(!sender.Status.Armed && sender.Status.Error != null,
                            "blocked native acquisition must expire independently of UI/control worker");
                        int stoppedCount = port.MotionCount;
                        release.Set(); Thread.Sleep(100);
                        Require(!sender.Status.Armed && port.MotionCount == stoppedCount,
                            "late acquisition/fresh input must never rearm after a lease expiry");
                    }
                    finally { release.Set(); }
                }
            }
        }
        private static void AcquisitionFailureStops()
        {
            OutputClock clock = new OutputClock(); Transport port = new Transport();
            using (ManualResetEvent failed = new ManualResetEvent(false))
            using (SerialOutput sender = new SerialOutput(clock, true))
            {
                sender.Connect(port); sender.Arm(); int calls = 0;
                using (ControlWorker input = new ControlWorker(delegate
                { Interlocked.Increment(ref calls); throw new InvalidOperationException("injected native failure"); },
                    delegate(Exception ex) { sender.InputFault(ex.Message); failed.Set(); }, true))
                {
                    Require(failed.WaitOne(1000), "input failure was not reported");
                    Thread.Sleep(60);
                    Require(!sender.Status.Armed && input.Error != null && !input.IsRunning && calls == 1,
                        "failed acquisition must disarm and never retry or restart automatically");
                }
            }
        }
        private static void StaleCompletionAndLongUptime()
        {
            Clock clock = new Clock(); Transport port = new Transport();
            using (SerialOutput sender = new SerialOutput(clock, false))
            {
                sender.Connect(port); sender.Arm();
                clock.Now = 25;
                long acquiredAt = clock.Now;
                // An input call that completes late must retain its old time.
                clock.Now = 130;
                sender.PublishAt(Motion, acquiredAt); sender.Pump();
                Require(!sender.Status.Armed && port.MotionCount == 0,
                    "slow acquisition completion must not relabel an expired sample as fresh");
                clock.Now = (long)int.MaxValue - 100;
                sender.Arm();
                for (int i = 0; i < 10000; i++)
                {
                    clock.Now += 25;
                    sender.PublishAt(Motion, clock.Now); sender.Pump();
                    Require(sender.Status.Armed, "healthy input has no int32 uptime or finite session cutoff");
                }
            }
        }
    }
}
