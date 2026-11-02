using System;
using System.IO;

namespace VestibularJoystickSim
{
    internal static class GameLauncherTests
    {
        internal static bool Run()
        {
            Require(GameLauncher.MatchesStartApp(true, "Forza Horizon 5", "Microsoft.624F8B84B80_8wekyb3d8bbwe!Forzahorizon5"), "Forza installed registration");
            Require(GameLauncher.MatchesStartApp(false, "Microsoft Flight Simulator", "Microsoft.FlightSimulator_8wekyb3d8bbwe!App"), "MSFS installed registration");
            Require(GameLauncher.MatchesStartApp(false, "Microsoft Flight Simulator 2024", "Microsoft.Limitless_8wekyb3d8bbwe!App"), "MSFS2024 installed registration");
            Require(!GameLauncher.MatchesStartApp(true, "Forza Horizon 5 Digital Ownership", "Microsoft.624F8B84B80_8wekyb3d8bbwe!Ownership"), "entitlement is not game launch");
            Require(!GameLauncher.MatchesStartApp(false, "Flight Simulator Companion", "Microsoft.Companion_8wekyb3d8bbwe!App"), "companion is not simulator");
            Require(!GameLauncher.MatchesStartApp(true, "Forza Horizon 5", "Microsoft.Game!App\" & bad"), "unvalidated shell argument");
            string root = Path.Combine(Path.GetTempPath(), "vforce-launch-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                string steamapps = Path.Combine(root, "steamapps");
                string folder = Path.Combine(steamapps, "common", "ForzaHorizon5");
                string manifest = Path.Combine(steamapps, "appmanifest_1551360.acf");
                Directory.CreateDirectory(folder);
                File.WriteAllText(manifest, "\"appid\" \"1551360\"\n\"StateFlags\" \"4\"\n\"installdir\" \"ForzaHorizon5\"");
                Require(GameLauncher.FindInstalledSteamLaunch(new string[] { root }, new string[] { "1551360" }) == "steam://run/1551360", "installed Steam game");
                File.WriteAllText(manifest, "\"appid\" \"1551360\"\n\"StateFlags\" \"2\"\n\"installdir\" \"ForzaHorizon5\"");
                Require(GameLauncher.FindInstalledSteamLaunch(new string[] { root }, new string[] { "1551360" }) == null, "incomplete install must not request install or purchase");
                File.WriteAllText(manifest, "\"appid\" \"1551360\"\n\"StateFlags\" \"4\"\n\"installdir\" \"..\\elsewhere\"");
                Require(GameLauncher.FindInstalledSteamLaunch(new string[] { root }, new string[] { "1551360" }) == null, "manifest path traversal");
                File.WriteAllText(manifest, "\"appid\" \"1250410\"\n\"StateFlags\" \"4\"\n\"installdir\" \"ForzaHorizon5\"");
                Require(GameLauncher.FindInstalledSteamLaunch(new string[] { root }, new string[] { "1551360" }) == null, "wrong installed game ID");
                File.Delete(manifest);
                Require(GameLauncher.FindInstalledSteamLaunch(new string[] { root }, new string[] { "1551360" }) == null, "missing game must not open store/install");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
            return true;
        }
        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Game launcher: " + message); }
    }
}
