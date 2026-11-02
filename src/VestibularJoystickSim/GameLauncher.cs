using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VestibularJoystickSim
{
    // Launch requests never establish telemetry readiness. Discovery and process
    // activation run outside both the UI and authoritative control workers.
    internal static class GameLauncher
    {
        private sealed class StartApp { internal string Name, Id; }
        internal static string ExplorerPath
        { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"); } }
        internal static string RequestLaunch(bool forza, Func<bool> current)
        {
            string name = forza ? "Forza Horizon 5" : "Flight Simulator";
            if (!current()) return "Launch superseded";
            string[] processes = forza ? new string[] { "ForzaHorizon5" } :
                new string[] { "FlightSimulator2024", "FlightSimulator" };
            foreach (string processName in processes)
            {
                foreach (Process process in Process.GetProcessesByName(processName))
                {
                    using (process)
                    {
                        if (!current()) return "Launch superseded";
                        if (process.HasExited) continue;
                        try
                        {
                            IntPtr window = process.MainWindowHandle;
                            if (window != IntPtr.Zero) { ShowWindow(window, 9); SetForegroundWindow(window); }
                        }
                        catch { /* A running game may not yet have a visible window. */ }
                        return name + " is already running - telemetry listening; waiting for live game data";
                    }
                }
            }

            string appId = null;
            string discoveryError = null;
            try
            {
                foreach (StartApp app in ReadStartApps())
                    if (MatchesStartApp(forza, app.Name, app.Id)) { appId = app.Id; break; }
            }
            catch (Exception ex) { discoveryError = ex.Message; }
            if (!current()) return "Launch superseded";
            if (appId != null)
            {
                ProcessStartInfo request = new ProcessStartInfo(ExplorerPath,
                    "shell:AppsFolder\\" + appId) { UseShellExecute = true };
                using (Process process = Process.Start(request)) { }
                return name + " launch requested - telemetry listening; waiting for live game data";
            }

            string steamUri = FindInstalledSteamLaunch(forza);
            if (!current()) return "Launch superseded";
            if (steamUri != null)
            {
                using (Process process = Process.Start(new ProcessStartInfo(steamUri) { UseShellExecute = true })) { }
                return name + " Steam launch requested - telemetry listening; waiting for live game data";
            }
            return name + " telemetry listening; no installed game launch entry found. Open the installed game manually" +
                (discoveryError == null ? "." : " (Windows app discovery failed: " + discoveryError + ").");
        }

        private static IEnumerable<StartApp> ReadStartApps()
        {
            // Get-StartApps returns the actual current-user registration, covering
            // Xbox/GDK installs without guessing package folders or fixed IDs.
            string command = "$ErrorActionPreference='Stop'; Get-StartApps | ForEach-Object { " +
                "[Console]::WriteLine(($_.Name -replace '[\\r\\n\\t]',' ') + [char]9 + $_.AppID) }";
            ProcessStartInfo start = new ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "WindowsPowerShell\\v1.0\\powershell.exe"),
                "-NoLogo -NoProfile -NonInteractive -Command \"" + command + "\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (Process process = Process.Start(start))
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(8000))
                { try { process.Kill(); } catch { } throw new TimeoutException("Get-StartApps exceeded 8 seconds"); }
                if (process.ExitCode != 0) throw new InvalidOperationException(error.Result.Trim());
                List<StartApp> result = new List<StartApp>();
                foreach (string line in output.Result.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int tab = line.IndexOf('\t');
                    if (tab > 0) result.Add(new StartApp { Name = line.Substring(0, tab), Id = line.Substring(tab + 1).Trim() });
                }
                return result;
            }
        }

        internal static bool MatchesStartApp(bool forza, string name, string id)
        {
            if (name == null || id == null || !Regex.IsMatch(id, @"^[A-Za-z0-9_.-]+![A-Za-z0-9_.-]+$")) return false;
            // Select only the installed game's registered identity, not Store,
            // ownership entitlements, companion apps or an arbitrary shortcut.
            if (forza) return name.Trim().Equals("Forza Horizon 5", StringComparison.OrdinalIgnoreCase) &&
                id.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase);
            return Regex.IsMatch(name.Trim(), @"^(Microsoft\s+)?Flight Simulator(\s+(2020|2024|40th Anniversary Edition))?$", RegexOptions.IgnoreCase) &&
                id.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase);
        }

        private static string FindInstalledSteamLaunch(bool forza)
        {
            HashSet<string> roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddRegistryRoot(roots, Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
            AddRegistryRoot(roots, Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
            AddRegistryRoot(roots, Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
            List<string> libraries = new List<string>(roots);
            foreach (string root in roots)
            {
                string file = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                try
                {
                    if (!File.Exists(file)) continue;
                    foreach (Match match in Regex.Matches(File.ReadAllText(file), @"""path""\s+""([^""]+)"""))
                        libraries.Add(match.Groups[1].Value.Replace(@"\\", @"\"));
                }
                catch { }
            }
            return FindInstalledSteamLaunch(libraries, forza ? new string[] { "1551360" } : new string[] { "2537590", "1250410" });
        }
        private static void AddRegistryRoot(HashSet<string> roots, RegistryKey hive, string key, string value)
        {
            try
            {
                using (RegistryKey entry = hive.OpenSubKey(key))
                {
                    string root = entry == null ? null : entry.GetValue(value) as string;
                    if (!string.IsNullOrEmpty(root) && Directory.Exists(root)) roots.Add(root);
                }
            }
            catch { }
        }
        internal static string FindInstalledSteamLaunch(IEnumerable<string> libraries, string[] ids)
        {
            foreach (string id in ids)
            foreach (string library in libraries)
            {
                try
                {
                    string path = Path.Combine(library, "steamapps", "appmanifest_" + id + ".acf");
                    if (!File.Exists(path)) continue;
                    string manifest = File.ReadAllText(path);
                    string directory = VdfValue(manifest, "installdir");
                    if (VdfValue(manifest, "appid") != id || VdfValue(manifest, "StateFlags") != "4" ||
                        string.IsNullOrEmpty(directory) || directory == "." || directory == ".." ||
                        directory.IndexOfAny(new char[] { '/', '\\', ':' }) >= 0) continue;
                    if (Directory.Exists(Path.Combine(library, "steamapps", "common", directory)))
                        return "steam://run/" + id;
                }
                catch { }
            }
            return null;
        }
        private static string VdfValue(string text, string key)
        {
            Match match = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : null;
        }
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    }
}
