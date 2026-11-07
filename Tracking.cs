using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LootLedger
{
    public enum MatchPhase { Lobby, Warmup, Aircraft, Playing, Finished }

    public sealed class MatchLog
    {
        private const string PhaseMarker =
            "LogBattleRoyaleGamePhaseLogic: HandleGamePhaseChanged.";
        private const string NewPhase = "NewPhase = EAthenaGamePhase::";
        private readonly string path;
        private readonly byte[] buffer = new byte[65536];
        private readonly byte[] header = new byte[128];
        private string identity;
        private long position;
        private string unfinished = "";

        public MatchPhase Phase { get; private set; }
        public string MatchId { get; private set; }
        public int MatchesSeen { get; private set; }

        public MatchLog(string path)
        {
            this.path = path;
        }

        public bool Poll()
        {
            try
            {
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    if (file.Read(header, 0, header.Length) != header.Length)
                        return false;

                    string currentIdentity;
                    using (var hash = SHA256.Create())
                        currentIdentity = Convert.ToBase64String(hash.ComputeHash(header));

                    if (identity != currentIdentity || file.Length < position)
                    {
                        identity = currentIdentity;
                        position = 0;
                        unfinished = "";
                        MatchId = null;
                        MatchesSeen = 0;
                        Phase = MatchPhase.Lobby;
                    }

                    file.Position = position;
                    long end = file.Length;
                    while (position < end)
                    {
                        int size = file.Read(buffer, 0, (int)Math.Min(buffer.Length, end - position));
                        if (size == 0)
                            break;
                        ReadChunk(size);
                        position += size;
                    }
                }
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private void ReadChunk(int size)
        {
            // Phase markers are ASCII. One character per byte keeps match IDs stable across reads.
            long chunkOffset = position - unfinished.Length;
            string chunk = unfinished + Encoding.ASCII.GetString(buffer, 0, size);
            int start = 0;
            int end;
            while ((end = chunk.IndexOf('\n', start)) >= 0)
            {
                ApplyLine(chunk, start, end - start, chunkOffset + start);
                start = end + 1;
            }
            unfinished = chunk.Substring(start);
        }

        private void ApplyLine(string chunk, int start, int length, long offset)
        {
            if (chunk.IndexOf(PhaseMarker, start, length, StringComparison.Ordinal) >= 0)
            {
                int phaseStart = chunk.IndexOf(NewPhase, start, length, StringComparison.Ordinal);
                if (phaseStart < 0)
                    return;
                phaseStart += NewPhase.Length;
                string phase = chunk.Substring(phaseStart, start + length - phaseStart).Trim();
                switch (phase)
                {
                    case "Warmup":
                        BeginMatch(offset, MatchPhase.Warmup);
                        break;
                    case "Aircraft":
                        if (Phase == MatchPhase.Warmup)
                            Phase = MatchPhase.Aircraft;
                        else if (Phase != MatchPhase.Aircraft)
                            BeginMatch(offset, MatchPhase.Aircraft);
                        break;
                    case "SafeZones":
                        if (Phase == MatchPhase.Warmup || Phase == MatchPhase.Aircraft || Phase == MatchPhase.Playing)
                            Phase = MatchPhase.Playing;
                        else
                            BeginMatch(offset, MatchPhase.Playing);
                        break;
                    case "EndGame":
                        Phase = MatchPhase.Finished;
                        break;
                }
            }
            else if (chunk.IndexOf("LogLoad: LoadMap: /Game/Maps/Frontend", start, length,
                StringComparison.Ordinal) >= 0 ||
                chunk.IndexOf("LogFort: MatchState changed previous=EnteringMap current=WaitingToStart",
                    start, length, StringComparison.Ordinal) >= 0)
            {
                Phase = MatchPhase.Lobby;
            }
            else if (chunk.IndexOf("LogFort: MatchState changed previous=InProgress current=WaitingPostMatch",
                start, length, StringComparison.Ordinal) >= 0)
            {
                Phase = MatchPhase.Finished;
            }
        }

        private void BeginMatch(long offset, MatchPhase phase)
        {
            MatchId = identity + "/" + offset;
            MatchesSeen++;
            Phase = phase;
        }
    }

    public sealed class HudLine
    {
        public string Text;
        public double X, Y, Height;

        public HudLine(string text, double x, double y, double height)
        {
            Text = text;
            X = x;
            Y = y;
            Height = height;
        }
    }

    public sealed class HudSignals
    {
        private static readonly Regex Xp = new Regex(@"(?:\+\s*)?\b\d[\d,.]{0,10}\s*X\s*P\b",
            RegexOptions.Compiled);
        private static readonly Regex ChestLabel = new Regex(
            @"^(?:(?:X ?)?(?<before>[2-9]|[1-9]\d) )?(?:(?:SEARCHED|OPENED) )?(?:(?:RARE|LEGENDARY|TREASURE|BURIED|BURIED TREASURE|PIRATE|CURSED|MYTHIC|SPECIAL) )?CHESTS?(?: (?:SEARCHED|OPENED))?(?: X ?(?<after>[2-9]|[1-9]\d))?$",
            RegexOptions.Compiled);
        private static readonly Regex Spectator = new Regex(
            @"^(?:SPECTATING(?: .{1,40})?|YOU PLACED \d+(?:ST|ND|RD|TH)?)$", RegexOptions.Compiled);

        public readonly List<ChestNotice> Chests = new List<ChestNotice>();
        private readonly List<HudLine> awards = new List<HudLine>();
        private readonly List<ChestNotice> labels = new List<ChestNotice>();
        public bool Spectating { get; private set; }
        public bool OnBattleBus { get; private set; }
        public bool Airborne { get; private set; }

        public static HudSignals Read(IEnumerable<HudLine> lines)
        {
            var result = new HudSignals();
            foreach (HudLine line in lines)
            {
                string words = Regex.Replace(line.Text.ToUpperInvariant().Replace('\u00d7', 'X'), @"[^A-Z0-9+,. ]", " ");
                words = Regex.Replace(words, @"\s+", " ").Trim();
                if (Spectator.IsMatch(words))
                    result.Spectating = true;
                if (Regex.IsMatch(words, @"\bTO JUMP\b|\bEVERYBODY OFF\b|\bDOORS WILL OPEN\b|\bLAST STOP IN\b"))
                    result.OnBattleBus = true;
                if (Regex.IsMatch(words, @"^(?:[A-Z0-9 ]{1,20} )?TO (?:DEPLOY(?: .{1,60})?|SKYDIVE)$"))
                    result.Airborne = true;

                if (line.X < 0.08 || line.X > 0.92 || line.Y < 0.06 || line.Y > 0.94)
                    continue;

                bool hasXp = Xp.IsMatch(words);
                string label = Xp.Replace(words, "").Trim(' ', '+', '.', ',');
                if (hasXp)
                    result.awards.Add(line);
                Match match = ChestLabel.Match(label);
                if (match.Success)
                {
                    string quantity = match.Groups["after"].Success ? match.Groups["after"].Value : match.Groups["before"].Value;
                    int count;
                    if (!int.TryParse(quantity, out count) || count > 20)
                        count = 1;
                    var notice = new ChestNotice(line, count);
                    int duplicate = result.labels.FindIndex(existing => SameLabel(existing.Line, line));
                    if (duplicate < 0)
                        result.labels.Add(notice);
                    else if (count > result.labels[duplicate].Count)
                        result.labels[duplicate] = notice;
                }
            }

            foreach (ChestNotice notice in result.labels)
                if (result.awards.Exists(award => Nearby(notice.Line, award)))
                    result.Chests.Add(notice);
            return result;
        }

        internal bool HasEvidence { get { return awards.Count > 0 || labels.Count > 0; } }

        internal bool HasEvidenceNear(HudLine line)
        {
            return awards.Exists(award => Nearby(line, award)) ||
                labels.Exists(label => Nearby(line, label.Line));
        }

        internal List<ChestNotice> ConfirmWith(HudSignals previous)
        {
            if (previous == null)
                return Chests;

            // During a turn the XP and chest label may be readable on different frames.
            var confirmed = new List<ChestNotice>(Chests);
            foreach (ChestNotice label in labels)
                if (!Chests.Contains(label) && previous.awards.Exists(award => Nearby(label.Line, award)))
                    confirmed.Add(label);

            if (labels.Count == 0)
                foreach (ChestNotice label in previous.labels)
                    if (!previous.Chests.Contains(label) && awards.Exists(award => Nearby(label.Line, award)))
                        confirmed.Add(label);
            return confirmed;
        }

        private static bool Nearby(HudLine first, HudLine second)
        {
            double tolerance = Math.Max(0.035, Math.Max(first.Height, second.Height) * 2.1);
            return Math.Abs(first.X - second.X) < 0.12 && Math.Abs(first.Y - second.Y) < tolerance;
        }

        private static bool SameLabel(HudLine first, HudLine second)
        {
            // Raw and enhanced OCR describe the same notice in normalized coordinates.
            return Math.Abs(first.X - second.X) < 0.12 &&
                Math.Abs(first.Y - second.Y) < Math.Max(0.008, Math.Min(first.Height, second.Height) * 0.6);
        }
    }

    public sealed class ChestNotice
    {
        public readonly HudLine Line;
        public readonly int Count;

        public ChestNotice(HudLine line, int count)
        {
            Line = line;
            Count = count;
        }
    }

    public sealed class ChestGate
    {
        private sealed class VisibleNotice
        {
            public ChestNotice Notice;
            public long EvidenceTime;
            public int ClearFrames;
        }

        private readonly List<VisibleNotice> visible = new List<VisibleNotice>();
        private readonly List<long> waitingAudio = new List<long>();
        private readonly List<long> countedAudio = new List<long>();
        private readonly List<long> countedHud = new List<long>();
        private HudSignals previousHud;
        private long previousHudTime;
        private long lastSpectator = -10000;
        private bool leftAircraft;
        public string Source { get; private set; }
        public bool TrackingAllowed { get; private set; }

        public void Reset()
        {
            visible.Clear();
            waitingAudio.Clear();
            countedAudio.Clear();
            countedHud.Clear();
            previousHud = null;
            lastSpectator = -10000;
            leftAircraft = false;
            TrackingAllowed = false;
            Source = null;
        }

        public void Suspend()
        {
            waitingAudio.Clear();
            countedAudio.Clear();
            countedHud.Clear();
            previousHud = null;
            TrackingAllowed = false;
        }

        public int Observe(long time, HudSignals hud, IEnumerable<long> audio, MatchPhase phase)
        {
            if ((phase != MatchPhase.Playing && phase != MatchPhase.Aircraft) ||
                (hud != null && hud.OnBattleBus))
            {
                Reset();
                return 0;
            }
            // SafeZones is global: an early jumper can already be looting during Aircraft.
            // The local deploy/skydive prompt proves that this player has left the bus.
            if (hud != null && hud.Airborne)
                leftAircraft = true;
            TrackingAllowed = phase == MatchPhase.Playing || leftAircraft;
            if (!TrackingAllowed)
                return 0;

            if (hud != null && hud.Spectating)
            {
                lastSpectator = time;
                waitingAudio.Clear();
                previousHud = null;
            }
            if (time - lastSpectator < 1400)
                return 0;

            countedAudio.RemoveAll(hit => time - hit > 1900);
            countedHud.RemoveAll(hit => time - hit > 1900);
            int count = 0;
            int visualCount = hud == null ? 0 : UpdateNotices(time, hud);
            for (int i = 0; i < visualCount; i++)
            {
                if (Consume(countedAudio, time))
                    continue;
                if (!Consume(waitingAudio, time))
                    countedHud.Add(time);
                count++;
                Source = "HUD";
            }

            foreach (long hit in audio)
                if (!Consume(countedHud, hit))
                    waitingAudio.Add(hit);

            while (waitingAudio.Count > 0 && time - waitingAudio[0] >= 950)
            {
                countedAudio.Add(waitingAudio[0]);
                waitingAudio.RemoveAt(0);
                count++;
                Source = count > 1 && visualCount > 0 ? "HUD + sound" : "Sound";
            }
            return count;
        }

        private int UpdateNotices(long time, HudSignals hud)
        {
            List<ChestNotice> notices = hud.ConfirmWith(time - previousHudTime <= 550 ? previousHud : null);
            if (hud.HasEvidence)
            {
                previousHud = hud;
                previousHudTime = time;
            }

            // OCR can drop a fading notice for several frames, especially in dark scenery.
            // Partial text refreshes its lifetime; a brief gap must not count it again.
            visible.RemoveAll(track => time - track.EvidenceTime > 1600 ||
                (track.ClearFrames >= 3 && time - track.EvidenceTime >= 1000));
            int count = 0;
            var available = new List<VisibleNotice>(visible);
            foreach (ChestNotice notice in notices)
            {
                VisibleNotice best = null;
                double distance = Double.MaxValue;
                foreach (VisibleNotice track in available)
                {
                    double x = Math.Abs(track.Notice.Line.X - notice.Line.X);
                    double y = Math.Abs(track.Notice.Line.Y - notice.Line.Y);
                    if (x < 0.13 && y < 0.09 && x + y < distance)
                    {
                        best = track;
                        distance = x + y;
                    }
                }
                if (best == null)
                {
                    visible.Add(new VisibleNotice { Notice = notice, EvidenceTime = time });
                    count += notice.Count;
                    continue;
                }
                available.Remove(best);
                count += Math.Max(0, notice.Count - best.Notice.Count);
                best.Notice = new ChestNotice(notice.Line, Math.Max(notice.Count, best.Notice.Count));
                best.EvidenceTime = time;
                best.ClearFrames = 0;
            }
            foreach (VisibleNotice track in available)
            {
                if (hud.HasEvidenceNear(track.Notice.Line))
                {
                    track.EvidenceTime = time;
                    track.ClearFrames = 0;
                }
                else
                    track.ClearFrames++;
            }
            return count;
        }

        private static bool Consume(List<long> hits, long time)
        {
            int best = -1;
            long distance = 1350;
            for (int i = 0; i < hits.Count; i++)
            {
                long gap = Math.Abs(hits[i] - time);
                if (gap < distance)
                {
                    best = i;
                    distance = gap;
                }
            }
            if (best == -1)
                return false;
            hits.RemoveAt(best);
            return true;
        }
    }

    public enum OverlayCorner { TopLeft, BottomLeft, TopRight, BottomRight }

    public static class GameWindow
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Point { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr window, ref Point point);
        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr window, int index, int style);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);

        private static bool KeyDown(int key)
        {
            return GetAsyncKeyState(key) < 0 || (key == 0x5B && GetAsyncKeyState(0x5C) < 0);
        }

        public static bool AnyShortcutKeyDown(int[] keys)
        {
            foreach (int key in keys)
                if (KeyDown(key)) return true;
            return false;
        }

        public static bool ShortcutDown(int[] keys)
        {
            if (keys == null || keys.Length == 0) return false;
            foreach (int key in keys)
                if (!KeyDown(key)) return false;
            // Unassigned modifiers change the chord; CapsLock remains a held key,
            // independent of its toggle state.
            foreach (int modifier in new[] { 0x10, 0x11, 0x12, 0x5B })
                if (KeyDown(modifier) != (Array.IndexOf(keys, modifier) >= 0)) return false;
            return true;
        }

        public static bool ShortcutDown(int key, uint modifiers)
        {
            if (GetAsyncKeyState(key) >= 0)
                return false;
            uint held = 0;
            if (GetAsyncKeyState(0x11) < 0) held |= 2; // Ctrl
            if (GetAsyncKeyState(0x12) < 0) held |= 1; // Alt
            if (GetAsyncKeyState(0x10) < 0) held |= 4; // Shift
            if (GetAsyncKeyState(0x5B) < 0 || GetAsyncKeyState(0x5C) < 0) held |= 8;
            return held == modifiers;
        }

        public static void MakeClickThrough(IntPtr window)
        {
            const int extendedStyle = -20;
            const int transparentToolWindow = 0x080000A0; // No activate, tool window, transparent.
            SetWindowLong(window, extendedStyle, GetWindowLong(window, extendedStyle) | transparentToolWindow);
        }

        public static void Position(IntPtr overlay, Rectangle area, OverlayCorner corner)
        {
            IntPtr previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
            try
            {
                Rect bounds;
                if (!GetWindowRect(overlay, out bounds))
                    return;

                int dpi = Math.Max((int)GetDpiForWindow(overlay), 96);
                int margin = 18 * dpi / 96;
                int width = bounds.Right - bounds.Left;
                int height = bounds.Bottom - bounds.Top;
                bool right = corner == OverlayCorner.TopRight || corner == OverlayCorner.BottomRight;
                bool bottom = corner == OverlayCorner.BottomLeft || corner == OverlayCorner.BottomRight;
                int x = right ? area.Right - width - margin : area.Left + margin;
                int y = bottom ? area.Bottom - height - margin : area.Top + margin;
                SetWindowPos(overlay, new IntPtr(-1), x, y, 0, 0, 0x0011);
            }
            finally
            {
                if (previous != IntPtr.Zero)
                    SetThreadDpiAwarenessContext(previous);
            }
        }

        public static Rectangle FocusedClient(out int processId)
        {
            processId = 0;
            IntPtr previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
            try
            {
                IntPtr window = GetForegroundWindow();
                GetWindowThreadProcessId(window, out processId);
                if (processId == 0)
                    return Rectangle.Empty;
                using (Process process = Process.GetProcessById(processId))
                    if (!process.ProcessName.Equals("FortniteClient-Win64-Shipping", StringComparison.OrdinalIgnoreCase))
                        return Rectangle.Empty;

                Rect rect;
                var origin = new Point();
                if (!GetClientRect(window, out rect) || !ClientToScreen(window, ref origin)
                    || rect.Right < 640 || rect.Bottom < 360)
                    return Rectangle.Empty;
                return new Rectangle(origin.X, origin.Y, rect.Right, rect.Bottom);
            }
            catch (ArgumentException)
            {
                return Rectangle.Empty; // The game closed between querying the window and process.
            }
            finally
            {
                if (previous != IntPtr.Zero)
                    SetThreadDpiAwarenessContext(previous);
            }
        }

        public static byte[] CaptureHud(Rectangle game)
        {
            int width = (int)(game.Width * 0.55);
            int height = (int)(game.Height * 0.88);
            IntPtr previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
            try
            {
                using (var frame = new Bitmap(width, height, PixelFormat.Format32bppRgb))
                using (var bmp = new MemoryStream())
                {
                    using (Graphics graphics = Graphics.FromImage(frame))
                        graphics.CopyFromScreen(game.Left + (game.Width - width) / 2,
                            game.Top + (int)(game.Height * 0.02), 0, 0, frame.Size);
                    int maxEdge = Math.Max(width, height);
                    if (maxEdge > 2400)
                    {
                        using (var smaller = new Bitmap(frame, width * 2400 / maxEdge, height * 2400 / maxEdge))
                            smaller.Save(bmp, ImageFormat.Bmp);
                    }
                    else
                        frame.Save(bmp, ImageFormat.Bmp);
                    return bmp.ToArray();
                }
            }
            finally
            {
                if (previous != IntPtr.Zero)
                    SetThreadDpiAwarenessContext(previous);
            }
        }
    }

    public static class HudImage
    {
        public static byte[] Enhance(byte[] capture)
        {
            using (var input = new MemoryStream(capture, false))
            using (var original = new Bitmap(input))
            {
                // Enlarge small HUD lettering, while staying within Windows OCR's image limit.
                int edge = Math.Max(original.Width, original.Height);
                double scale = Math.Min(2.0, 2400.0 / edge);
                int width = Math.Max(1, (int)(original.Width * scale));
                int height = Math.Max(1, (int)(original.Height * scale));
                using (var image = new Bitmap(width, height, PixelFormat.Format32bppRgb))
                using (var output = new MemoryStream())
                {
                    using (Graphics graphics = Graphics.FromImage(image))
                    {
                        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        graphics.DrawImage(original, 0, 0, width, height);
                    }
                    var area = new Rectangle(0, 0, width, height);
                    BitmapData data = image.LockBits(area, ImageLockMode.ReadWrite, PixelFormat.Format32bppRgb);
                    try
                    {
                        var row = new byte[width * 4];
                        for (int y = 0; y < height; y++)
                        {
                            IntPtr address = IntPtr.Add(data.Scan0, y * data.Stride);
                            Marshal.Copy(address, row, 0, row.Length);
                            for (int x = 0; x < row.Length; x += 4)
                            {
                                // White and gold XP lettering becomes dark ink on a white background.
                                // Use the brighter red/green channel so gold text survives dark scenes.
                                int brightness = Math.Max(row[x + 1], row[x + 2]);
                                byte ink = (byte)(255 - Math.Min(255, Math.Max(0, brightness - 85) * 255 / 130));
                                row[x] = row[x + 1] = row[x + 2] = ink;
                            }
                            Marshal.Copy(row, 0, address, row.Length);
                        }
                    }
                    finally { image.UnlockBits(data); }
                    image.Save(output, ImageFormat.Bmp);
                    return output.ToArray();
                }
            }
        }
    }
}
