using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NrgToMp3;

namespace NrgToMp3Win
{
    /// <summary>One song on the burn list: an audio file, a song inside a disc image, or audio made inside the app
    /// (split parts, key changes).</summary>
    class BurnItem
    {
        public Guid Id = Guid.NewGuid();
        public string Title, Note;
        public double Seconds;
        public double? Gap;                 // seconds of silence before this song; null = the disc's default
        public string File;                 // an audio file
        public string Image;                // or the audio file of a disc image (.nrg, or a .cue's .bin)
        public PlaylistTrack Track;
        public bool Swap;
        public byte[] Pcm;                  // or CD audio held in memory
        public Func<byte[]> Loader;         // or read some other way (a song on a CD in the drive)

        public string Source => Note ?? (File != null ? Path.GetFileName(File) : Image != null ? $"{Path.GetFileName(Image)}, track {Track.number}" : "Made in this app");

        /// <summary>The song as CD audio (44.1 kHz / 16-bit / stereo, little-endian). <paramref name="decode"/> reads audio files.</summary>
        public byte[] Load(Func<string, byte[]> decode)
        {
            if (Pcm != null) return Pcm;
            if (Loader != null) return Loader();
            if (File != null) return decode(File);
            var t = new Nrg.Track { Number = Track.number, SectorSize = Track.sectorSize, Mode = 7, Start = Track.start, End = Track.start + Track.sectors * Track.sectorSize };
            return Audio.ReadSong(Image, t, Swap);
        }

        public static BurnItem FromPlayer(PlayerSong s) => new()
        {
            Title = s.Title, Note = s.Note, Seconds = s.Seconds, Gap = s.Gap, File = s.File, Image = s.Image, Track = s.Track, Swap = s.Swap, Loader = s.Loader,
        };
    }

    /// <summary>The songs to burn, shared by every tab ("Add to Burn List").</summary>
    class BurnList
    {
        public static readonly BurnList Shared = new();
        public static readonly double[] GapChoices = { 0, 0.5, 1, 2, 3, 4, 5 };
        public const double Capacity = 80 * 60;
        public const double LeadIn = 2;      // every CD starts with a 2 s pause before the first song (CD standard)
        public const int Sector = 2352;

        public readonly List<BurnItem> Items = new();
        public string DiscTitle = "", DiscArtist = "";

        /// <summary>The list's track layout as it would be burned, for MusicBrainz. Marked inexact so near matches are
        /// found too: song lengths from MP3s etc. can be a little off.</summary>
        public Toc Toc()
        {
            if (Items.Count == 0) return null;
            int pos = 150;
            var offsets = new List<int>();
            for (int i = 0; i < Items.Count; i++)
            {
                pos += (int)Sectors(GapAt(i));
                offsets.Add(pos);
                pos += (int)Sectors(Items[i].Seconds);
            }
            return new Toc(false, 1, Items.Count, pos, offsets.ToArray());
        }
        public double DefaultGap = double.TryParse(Settings.Get("defaultGap", "2"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var g) ? g : 2;
        public event Action Changed;

        public void SetDefaultGap(double g) { DefaultGap = g; Settings.Set("defaultGap", g.ToString(System.Globalization.CultureInfo.InvariantCulture)); Changed?.Invoke(); }
        public void NotifyChanged() => Changed?.Invoke();

        public static string GapLabel(double g) => g == 0 ? "None (seamless)" : $"{g.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} s";

        public double GapAt(int i) => i == 0 ? 0 : Items[i].Gap ?? DefaultGap;
        public double TotalSeconds => LeadIn + Enumerable.Range(0, Items.Count).Sum(i => GapAt(i) + Items[i].Seconds);

        public void Add(IEnumerable<BurnItem> items) { Items.AddRange(items); Changed?.Invoke(); }

        /// <summary>Every song of a disc image with its original gaps, so a burned copy matches the CD it came from.</summary>
        public static List<BurnItem> FromImage(DiscItem d) => d.Songs.Select(t => new BurnItem
        {
            Title = d.Name(t), Seconds = t.Sectors / 75.0, Image = d.Nrg.DataPath, Swap = d.Swap, Gap = Math.Round(t.GapSeconds * 75) / 75,
            Track = new PlaylistTrack { number = t.Number, title = d.Name(t), seconds = t.Sectors / 75.0, start = t.Start, sectors = t.Sectors, sectorSize = t.SectorSize, gap = t.GapSeconds },
        }).ToList();

        /// <summary>The ticked songs of a disc (Convert tab), with the disc's default gap.</summary>
        public static List<BurnItem> FromTicked(DiscItem d) => d.Songs.Where(t => d.Selected.Contains(t.Number)).Select(t => new BurnItem
        {
            Title = d.Name(t), Seconds = t.Sectors / 75.0, Image = d.Nrg.DataPath, Swap = d.Swap,
            Track = new PlaylistTrack { number = t.Number, title = d.Name(t), seconds = t.Sectors / 75.0, start = t.Start, sectors = t.Sectors, sectorSize = t.SectorSize },
        }).ToList();

        public static string NumberedName(int number, string name)
        {
            if (!Settings.GetBool("numberNames")) return name;
            var prefix = $"{number:00} - ";
            return name.StartsWith(prefix) ? name : prefix + name;
        }

        /// <summary>Why the list can't be burned as it is, or null.</summary>
        public string Problem()
        {
            if (Items.Count == 0) return "The list is empty.";
            if (Items.Count > 99) return "A CD holds at most 99 songs.";
            if (TotalSeconds > Capacity) return "The list is too long for one CD (80 minutes).";
            for (int i = 0; i < Items.Count; i++)
                if (Items[i].Seconds + (i + 1 < Items.Count ? GapAt(i + 1) : 0) < 4)
                    return $"Song {i + 1} (“{Items[i].Title}”) is shorter than 4 seconds, the shortest track a CD allows.";
            return null;
        }

        static void Pad(Stream s, long bytes)
        {
            int pad = (int)((Sector - bytes % Sector) % Sector);   // songs must end on a whole sector
            if (pad > 0) s.Write(new byte[pad], 0, pad);
        }

        static void Silence(Stream s, long sectors)
        {
            var zero = new byte[Sector * 75];
            for (long left = sectors * Sector; left > 0; left -= zero.Length) s.Write(zero, 0, (int)Math.Min(left, zero.Length));
        }

        /// <summary>A gap in CD sectors (1/75 s), rounded like the Mac app.</summary>
        static long Sectors(double seconds) => (long)Math.Round(seconds * 75, MidpointRounding.AwayFromZero);

        static string Msf(long sectors) => $"{sectors / 4500:00}:{sectors / 75 % 60:00}:{sectors % 75:00}";
        static string Quoted(string s) => "\"" + (s ?? "").Replace("\"", "'") + "\"";

        /// <summary>Writes a CUE sheet and its raw BIN. Each song after the first gets exactly its gap of silence
        /// as INDEX 00 (pregap), so a gap of 0 plays straight through with no pause.</summary>
        public void WriteImage(string cue, Func<string, byte[]> decode, Action<string> progress)
        {
            string bin = Path.ChangeExtension(cue, ".bin");
            var text = new StringBuilder();
            if (DiscArtist != "") text.Append($"PERFORMER {Quoted(DiscArtist)}\r\n");
            if (DiscTitle != "") text.Append($"TITLE {Quoted(DiscTitle)}\r\n");
            text.Append($"FILE {Quoted(Path.GetFileName(bin))} BINARY\r\n");
            long pos = 0;    // in sectors
            using (var f = System.IO.File.Create(bin))
                for (int i = 0; i < Items.Count; i++)
                {
                    var item = Items[i];
                    progress?.Invoke($"Preparing {i + 1} of {Items.Count}: {item.Title}");
                    text.Append($"  TRACK {i + 1:00} AUDIO\r\n    TITLE {Quoted(NumberedName(i + 1, item.Title))}\r\n");
                    long gap = Sectors(GapAt(i));
                    if (gap > 0) { text.Append($"    INDEX 00 {Msf(pos)}\r\n"); Silence(f, gap); pos += gap; }
                    text.Append($"    INDEX 01 {Msf(pos)}\r\n");
                    var pcm = item.Load(decode);
                    f.Write(pcm, 0, pcm.Length);
                    Pad(f, pcm.Length);
                    pos += (pcm.Length + Sector - 1) / Sector;
                }
            System.IO.File.WriteAllText(cue, text.ToString());
        }

        /// <summary>One raw audio file per CD track for the burner: each song followed by the silence before the next
        /// one, so the songs sit exactly that far apart on the disc (no gap = seamless).</summary>
        public List<string> WriteTracks(string folder, Func<string, byte[]> decode, Action<string> progress, bool withGaps = true)
        {
            var files = new List<string>();
            for (int i = 0; i < Items.Count; i++)
            {
                progress?.Invoke($"Preparing {i + 1} of {Items.Count}: {Items[i].Title}");
                string path = Path.Combine(folder, $"{i + 1:00}.pcm");
                using (var f = System.IO.File.Create(path))
                {
                    var pcm = Items[i].Load(decode);
                    f.Write(pcm, 0, pcm.Length);
                    Pad(f, pcm.Length);
                    if (withGaps && i + 1 < Items.Count) Silence(f, Sectors(GapAt(i + 1)));
                }
                files.Add(path);
            }
            return files;
        }

        /// <summary>Saves the list as a playlist (the Mac's format). Songs that only exist inside the app are written as
        /// WAV files into a "&lt;playlist&gt; files" folder next to it. Returns how many were written that way.</summary>
        public int SavePlaylist(string path)
        {
            string filesDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFileNameWithoutExtension(path) + " files");
            int written = 0;
            var songs = new List<PlayerSong>();
            for (int i = 0; i < Items.Count; i++)
            {
                var it = Items[i];
                var s = new PlayerSong { Title = it.Title, Note = it.Note, Gap = it.Gap, Seconds = it.Seconds, File = it.File, Image = it.Image, Track = it.Track, Swap = it.Swap };
                if (it.Pcm != null || it.Loader != null)
                {
                    Directory.CreateDirectory(filesDir);
                    string wav = Path.Combine(filesDir, $"{i + 1:00} - {Program.Clean(it.Title)}.wav");
                    WriteWav(wav, it.Pcm ?? it.Loader());
                    s.File = wav;
                    written++;
                }
                songs.Add(s);
            }
            Playlists.Save(path, DiscTitle, DefaultGap, songs);
            return written;
        }

        public static void WriteWav(string path, byte[] pcm)
        {
            using var f = System.IO.File.Create(path);
            using var w = new BinaryWriter(f);
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + pcm.Length); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(44100); w.Write(176400); w.Write((short)4); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(pcm.Length); w.Write(pcm);
        }

        /// <summary>Loads a playlist, replacing the list or adding to its end. Returns the songs that couldn't be found.</summary>
        public List<string> LoadPlaylist(string path, bool replace, Func<string, double> secondsOfFile)
        {
            var (title, defaultGap, songs) = Playlists.Load(path, out var missing, secondsOfFile);
            var loaded = songs.Select(BurnItem.FromPlayer).ToList();
            if (replace) { Items.Clear(); DiscTitle = title ?? ""; DefaultGap = defaultGap; Settings.Set("defaultGap", defaultGap.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            else if (DiscTitle == "") DiscTitle = title ?? "";
            Items.AddRange(loaded);
            Changed?.Invoke();
            return missing;
        }
    }
}
