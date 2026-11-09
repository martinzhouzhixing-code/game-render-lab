using System;
using System.IO;
using System.Linq;

namespace GameRenderLab.Endfield
{
    internal static class SelfTest
    {
        private static void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL " + name);
        }

        internal static int Run(string report)
        {
            string temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report)), "fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            try
            {
                var fresh = new IniText(Deployment.ConfigureIni(""));
                Check(fresh.Value("OVERLAY", "Language") == "zh-CN" && fresh.Value("OVERLAY", "TutorialProgress") == "4", "ReShade Chinese font and welcome state");
                Check(fresh.Value("RenoDX.DLSS5", "UiLanguage") == "zh-Hans" && fresh.Value("RenoDX.DLSS5", "NRUpscaledPasses") == "1" &&
                    fresh.Value("RenoDX.DLSS5", "NRPresentPasses") == "0" && fresh.Value("RenoDX.DLSS5", "NRResolutionScale") == "0.5", "fresh safe NR defaults");
                var existing = new IniText(Deployment.ConfigureIni("[OTHER]\nKeep=1\n[RenoDX.DLSS5]\nNRUpscaledPasses=2\n"));
                Check(existing.Value("OTHER", "Keep") == "1" && existing.Value("RenoDX.DLSS5", "NRUpscaledPasses") == "2", "preserve user choices");
                Check(Deployment.DriverAtLeast615("32.0.16.2114") && !Deployment.DriverAtLeast615("32.0.15.9999"), "driver version mapping");
                string[] appPaths = { @"C:\Other game\Game.exe", @"D:\Game, Beta\Endfield.exe" };
                Check(ReShadeLayer.ParseList(ReShadeLayer.SerializeList(appPaths)).SequenceEqual(appPaths), "ReShade application allowlist escaping");
                bool escaped = false;
                try { FileSafety.Under(temporary, "../outside"); } catch (IOException) { escaped = true; }
                Check(escaped, "relative path traversal rejected");
                bool rejected = false;
                try { EndfieldLocation.Verify(temporary); } catch (IOException) { rejected = true; }
                Check(rejected, "fake game directory rejected");

                string game = Path.Combine(temporary, "game");
                string store = Path.Combine(temporary, "store");
                Directory.CreateDirectory(game);
                Directory.CreateDirectory(store);
                string ini = Path.Combine(game, "ReShade.ini");
                string bridge = Path.Combine(game, "dlss5-bridge.addon64");
                string generic = Path.Combine(game, "renodx-dlss5.addon64");
                File.WriteAllText(ini, "original settings");
                File.WriteAllText(bridge, "original bridge");
                var journal = InstallJournal.Begin(store, game, false);
                File.WriteAllText(ini, "installed settings");
                File.Delete(bridge);
                File.WriteAllText(generic, "installer file");
                journal.Complete();
                File.WriteAllText(generic, "new external file");
                bool refused = false;
                try { InstallJournal.Open(store, game).Restore(false); } catch (IOException) { refused = true; }
                Check(refused && File.ReadAllText(ini) == "installed settings", "rollback refuses modified binaries before writing");
                File.WriteAllText(generic, "installer file");
                InstallJournal.Open(store, game).Restore(false);
                Check(File.ReadAllText(ini) == "original settings" && File.ReadAllText(bridge) == "original bridge" && !File.Exists(generic), "rollback restores and removes managed files");
                Check(!InstallJournal.Exists(store), "transaction pointer cleared");
                Deployment.StageCandidate(temporary, "renodx-dlss5.addon64", BuildPayload.GenericHash);
                Deployment.StageCandidate(temporary, "renodx-mfgunlock.addon64", BuildPayload.MfgHash);
                File.WriteAllText(report, "PASS Chinese defaults; driver version; allowlist; path traversal; fake game rejection; safe rollback; embedded x64 add-ons.");
                return 0;
            }
            catch (Exception error) { File.WriteAllText(report, error.ToString()); return 1; }
        }
    }
}
