using System;
using System.Diagnostics;
using System.Threading;

namespace VestibularJoystickSim
{
    // Acquisition and control run without the Forms message pump. A slow or
    // failed acquisition never bypasses the independent serial snapshot lease.
    internal sealed class ControlWorker : IDisposable
    {
        private readonly Action step;
        private readonly Action<Exception> fail;
        private readonly ManualResetEvent stop = new ManualResetEvent(false);
        private readonly Thread worker;
        private volatile bool stopping;
        private volatile Exception error;
        private int disposed;
        internal Exception Error { get { return error; } }
        internal bool IsRunning { get { return worker != null && worker.IsAlive && !stopping && error == null; } }
        internal ControlWorker(Action step, Action<Exception> fail, bool start)
        {
            this.step = step;
            this.fail = fail;
            if (start)
            {
                worker = new Thread(Run) { IsBackground = true, Name = "VFORCE input and control" };
                worker.Start();
            }
        }
        private void Run()
        {
            try
            {
                while (!stopping)
                {
                    long began = Stopwatch.GetTimestamp();
                    step();
                    long elapsed = (Stopwatch.GetTimestamp() - began) * 1000 / Stopwatch.Frequency;
                    if (stop.WaitOne((int)Math.Max(1L, 25L - elapsed))) break;
                }
            }
            catch (Exception ex)
            {
                error = ex;
                // Never retry an acquisition failure or silently restart output.
                try { fail(ex); } catch { }
            }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            stopping = true;
            stop.Set();
            if (worker == null || (worker != Thread.CurrentThread && worker.Join(1000))) stop.Dispose();
        }
    }
}
