using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace LootLedger.Tests
{
    public static class TrackingTests
    {
        private static int checks;
        private static void Equal<T>(T expected, T actual, string scenario)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception(scenario + ": expected " + expected + ", got " + actual);
            checks++;
        }

        private static HudSignals Hud(params string[] texts)
        {
            var lines = new List<HudLine>();
            for (int i = 0; i < texts.Length; i++)
                lines.Add(new HudLine(texts[i], 0.5, 0.45 + i * 0.025, 0.02));
            return HudSignals.Read(lines);
        }

        private static int See(ChestGate gate, long time, HudSignals hud)
        {
            return gate.Observe(time, hud, new long[0], MatchPhase.Playing);
        }

        public static string Run()
        {
            checks = 0;
            HudSignals chest = Hud("+250 XP", "CHEST");
            var gate = new ChestGate();
            Equal(0, See(gate, 0, Hud("+100 XP", "THANK THE BUS DRIVER")), "Generic XP is excluded");
            gate.Reset();
            Equal(0, See(gate, 135, Hud("CHEST")), "Chest text without XP is excluded");
            gate.Reset();

            Equal(0, gate.Observe(0, chest, new long[] { 0 }, MatchPhase.Aircraft), "Bus rejects even chest-like XP and sound");
            Equal(0, gate.Observe(1500, Hud(), new long[0], MatchPhase.Aircraft), "Bus sound cannot age into a count");
            Equal(false, gate.TrackingAllowed, "Tracking stays off on bus");
            gate.Observe(1600, Hud("SPACE TO DEPLOY UMBRELLA"), new long[0], MatchPhase.Aircraft);
            Equal(true, gate.TrackingAllowed, "An early jump enables tracking");
            Equal(1, gate.Observe(1800, chest, new long[0], MatchPhase.Aircraft), "Early jumper can loot before SafeZones");
            gate.Suspend();
            Equal(0, gate.Observe(1900, chest, new long[0], MatchPhase.Aircraft), "Focus change preserves notice and jump state");
            Equal(true, gate.TrackingAllowed, "Jump state survives focus change");
            Equal(0, gate.Observe(2000, Hud("SPACE TO JUMP", "+250 XP", "CHEST"), new long[] { 2000 }, MatchPhase.Playing), "Visible bus prompt overrides phase");
            Equal(false, gate.TrackingAllowed, "Bus prompt disables tracking");
            Equal(1, gate.Observe(3500, chest, new long[0], MatchPhase.Playing), "SafeZones enables tracking without seeing jump");
            Equal(0, gate.Observe(3600, chest, new long[] { 3550 }, MatchPhase.Playing), "HUD and sound count once");
            Equal(0, See(gate, 4800, Hud()), "Matched sound cannot count later");
            gate.Reset();

            var passes = new List<HudLine> {
                new HudLine("+250 XP", 0.5, 0.42, 0.02),
                new HudLine("CHEST", 0.5, 0.45, 0.02),
                new HudLine("+250 XP", 0.502, 0.421, 0.02),
                new HudLine("+250 XP CHEST", 0.56, 0.451, 0.02),
                new HudLine("+250 XP", 0.5, 0.58, 0.02),
                new HudLine("RARE CHEST", 0.5, 0.61, 0.02)
            };
            Equal(2, See(gate, 0, HudSignals.Read(passes)), "Two OCR passes deduplicate while preserving separate chests");
            gate.Reset();
            Equal(1, See(gate, 0, chest), "First notice counts");
            See(gate, 135, Hud());
            See(gate, 270, Hud());
            See(gate, 405, Hud());
            See(gate, 540, Hud());
            Equal(0, See(gate, 675, chest), "OCR dropout does not count same notice twice");
            Equal(0, See(gate, 1000, Hud("CHEST")), "Fading partial notice stays tracked");
            See(gate, 1400, Hud("CHEST"));
            See(gate, 1800, Hud("CHEST"));
            Equal(0, See(gate, 2200, chest), "Partial evidence prevents timeout and duplicate");
            Equal(1, See(gate, 2300, Hud("+500 XP", "CHEST x2")), "Quantity increment adds only new chest");
            Equal(0, See(gate, 2400, chest), "Quantity reduction is ignored");
            Equal(0, See(gate, 2500, Hud("+500 XP", "CHEST x2")), "Quantity rebound is ignored");
            See(gate, 2700, Hud());
            See(gate, 3000, Hud());
            See(gate, 3300, Hud());
            See(gate, 3600, Hud());
            Equal(1, See(gate, 3800, chest), "New chest after clear interval counts");
            gate.Reset();
            See(gate, 0, Hud("+250 XP"));
            Equal(1, See(gate, 400, Hud("CHEST")), "XP and label can be recognized on adjacent frames");
            Equal(0, See(gate, 500, Hud("SPECTATING PLAYER", "+250 XP", "CHEST")), "Spectator notices are ignored");
            Equal(0, See(gate, 1200, chest), "Spectator cooldown is retained");

            string logPath = Path.GetTempFileName();
            try
            {
                File.WriteAllText(logPath, new string(' ', 128) + "\n");
                var log = new MatchLog(logPath);
                AppendPhase(logPath, "Warmup");
                Equal(true, log.Poll(), "Read match log");
                string id = log.MatchId;
                AppendPhase(logPath, "Aircraft");
                log.Poll();
                Equal(MatchPhase.Aircraft, log.Phase, "Aircraft has its own phase");
                Equal(id, log.MatchId, "Bus preserves match identity");
                AppendPhase(logPath, "SafeZones");
                log.Poll();
                Equal(MatchPhase.Playing, log.Phase, "SafeZones starts play");
                Equal(id, log.MatchId, "SafeZones preserves match identity");
                Equal(1, log.MatchesSeen, "One match counted across flight");
                AppendPhase(logPath, "EndGame");
                log.Poll();
                Equal(MatchPhase.Finished, log.Phase, "Match ends");
                AppendPhase(logPath, "Warmup");
                log.Poll();
                Equal(2, log.MatchesSeen, "Next match starts once");
            }
            finally { File.Delete(logPath); }

            using (var image = new Bitmap(400, 200))
            using (var graphics = Graphics.FromImage(image))
            using (var raw = new MemoryStream())
            {
                graphics.Clear(Color.FromArgb(15, 15, 15));
                using (var gold = new SolidBrush(Color.FromArgb(180, 145, 60)))
                    graphics.FillRectangle(gold, 100, 50, 100, 60);
                image.Save(raw, ImageFormat.Bmp);
                using (var enhancedStream = new MemoryStream(HudImage.Enhance(raw.ToArray())))
                using (var enhanced = new Bitmap(enhancedStream))
                {
                    Equal(800, enhanced.Width, "Small lettering is enlarged");
                    Equal(400, enhanced.Height, "Aspect ratio is preserved");
                    int background = enhanced.GetPixel(20, 20).R;
                    int lettering = enhanced.GetPixel(250, 150).R;
                    Equal(true, background - lettering > 160, "Dim gold gains contrast on dark background");
                }
            }
            return "PASS: " + checks + " tracking regression checks";
        }

        private static void AppendPhase(string path, string phase)
        {
            File.AppendAllText(path, "LogBattleRoyaleGamePhaseLogic: HandleGamePhaseChanged. NewPhase = EAthenaGamePhase::" + phase + "\n");
        }
    }
}
