using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;

namespace VestibularJoystickSim
{
    // Disk activity never runs on the control/UI thread or serial sender.
    internal static class RuntimeDiagnostics
    {
        private static readonly object gate = new object();
        private static readonly Queue<string> pending = new Queue<string>();
        private static readonly AutoResetEvent wake = new AutoResetEvent(false);
        private static Thread writer;
        private static bool stopping;
        internal static string LogPath { get; private set; }
        internal static void Start()
        {
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VFORCE", "Logs");
                Directory.CreateDirectory(folder);
                LogPath = Path.Combine(folder, "session-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".log");
                writer = new Thread(WriteLoop) { IsBackground = true, Name = "VFORCE diagnostics" };
                writer.Start();
                Record("START version=" + Assembly.GetExecutingAssembly().GetName().Version + " exe=" + Assembly.GetExecutingAssembly().Location);
            }
            catch { writer = null; LogPath = "unavailable"; }
        }
        internal static void Record(string value)
        {
            if (writer == null) return;
            lock (gate)
            {
                if (stopping) return;
                if (pending.Count >= 256) pending.Dequeue();
                pending.Enqueue(DateTime.UtcNow.ToString("o") + " " + value.Replace("\r", " ").Replace("\n", " | "));
                wake.Set();
            }
        }
        private static void WriteLoop()
        {
            try
            {
                using (StreamWriter output = new StreamWriter(LogPath, true))
                {
                    while (true)
                    {
                        string[] batch; bool done;
                        lock (gate) { batch = pending.ToArray(); pending.Clear(); done = stopping; }
                        foreach (string line in batch) output.WriteLine(line);
                        output.Flush();
                        if (done) return;
                        wake.WaitOne(500);
                    }
                }
            }
            catch { /* Diagnostics must never stop simulation or output shutdown. */ }
        }
        internal static bool SelfTest()
        {
            Start();
            Record("TEST_FAULT saved without resuming output");
            Stop();
            try { return File.ReadAllText(LogPath).Contains("TEST_FAULT saved without resuming output"); }
            catch { return false; }
        }
        internal static void Stop()
        {
            lock (gate) { stopping = true; wake.Set(); }
            if (writer != null && Thread.CurrentThread != writer) writer.Join(1000);
        }
    }
}
