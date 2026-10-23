using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NrgToMp3;

namespace NrgToMp3Win
{
    /// <summary>One opened disc image and the choices made for it.</summary>
    class DiscItem
    {
        public string Path;
        public bool Loading = true;
        public string Error, Note;
        public Nrg Nrg;
        public List<Nrg.Track> Songs = new();
        public bool Swap;
        public Toc Toc;
        public string Album = "", Artist = "";
        public string MergeName = "";                      // "Merge into one file": the file's name (empty = the album)
        public Dictionary<int, string> Names = new();     // song names, edited or looked up
        public HashSet<int> Selected = new();
        public string NamesNote;                           // e.g. "Names loaded from Church Music.txt"

        /// <summary>The names file next to the image: "Church Music.nrg" → "Church Music.txt".</summary>
        public string NamesFile => System.IO.Path.ChangeExtension(Path, ".txt");

        /// <summary>Album, artist and song names as a simple text file anyone can write by hand.</summary>
        public string NamesText()
        {
            var lines = new List<string>
            {
                $"# Song names for {System.IO.Path.GetFileName(Path)}",
                "# One song per line: a number, then the name. Lines starting with # are ignored.",
            };
            if (Album != "") lines.Add($"Album: {Album}");
            if (Artist != "") lines.Add($"Artist: {Artist}");
            lines.AddRange(Songs.Select(t => $"{t.Number}. {Name(t)}"));
            return string.Join("\r\n", lines) + "\r\n";
        }

        /// <summary>Reads a names file (see NamesText). Numbered lines go to that song; lines without a number
        /// go to the songs in order. Returns how many songs got a name.</summary>
        public int ApplyNames(string text)
        {
            var numbered = new Dictionary<int, string>();
            var plain = new List<string>();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line == "" || line.StartsWith("#")) continue;
                string Rest(string prefix) => line.Substring(prefix.Length).Trim();
                var lower = line.ToLowerInvariant();
                if (lower.StartsWith("album:")) { Album = Rest("album:"); continue; }
                if (lower.StartsWith("title:")) { Album = Rest("title:"); continue; }
                if (lower.StartsWith("artist:")) { Artist = Rest("artist:"); continue; }
                if (lower.StartsWith("performer:")) { Artist = Rest("performer:"); continue; }
                // "1. Name", "01 - Name", "1) Name", "1 Name"
                var m = System.Text.RegularExpressions.Regex.Match(line, @"^(\d{1,2})\s*[.):\-]?\s+(.+)$");
                if (m.Success) numbered[int.Parse(m.Groups[1].Value)] = m.Groups[2].Value.Trim();
                else plain.Add(line);
            }
            int count = 0;
            for (int i = 0; i < Songs.Count; i++)
            {
                var t = Songs[i];
                if (numbered.TryGetValue(t.Number, out var n)) { Names[t.Number] = n; count++; }
                else if (numbered.Count == 0 && i < plain.Count) { Names[t.Number] = plain[i]; count++; }
            }
            return count;
        }

        static string ReadText(string file)
        {
            var bytes = File.ReadAllBytes(file);
            try { return new System.Text.UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
            catch { return System.Text.Encoding.Latin1.GetString(bytes); }      // older Notepad files
        }

        public int LoadNames(string file) => ApplyNames(ReadText(file));

        /// <summary>Default name for "Merge into one file": the first ticked song's name.</summary>
        public string FirstSongName => Songs.FirstOrDefault(t => Selected.Contains(t.Number)) is { } t ? Name(t) : Album;

        public string Name(Nrg.Track t) =>
            Names.TryGetValue(t.Number, out var n) && !string.IsNullOrWhiteSpace(n) ? n.Trim() : (t.Title ?? $"Track {t.Number:00}");

        public override string ToString() =>
            System.IO.Path.GetFileName(Path) + (Loading ? "   (reading…)" : Error != null ? "   ⚠" : $"   ({Songs.Count} songs)");

        public void Load()
        {
            try
            {
                Nrg = Nrg.Read(Path);
                Songs = Nrg.Tracks.Where(t => t.IsAudio).ToList();
                if (Songs.Count == 0) throw new InvalidDataException("This image has no music tracks (it's a data disc).");
                using (var f = File.OpenRead(Nrg.DataPath)) Swap = Program.IsBigEndian(f, Songs[0]);
                Album = Nrg.Album ?? System.IO.Path.GetFileNameWithoutExtension(Path);
                Artist = Nrg.Performer ?? Songs[0].Performer ?? "";
                Note = Nrg.IsRawAudio ? "No Nero track list in this file; songs were split at silences." : null;
                Names = Songs.ToDictionary(t => t.Number, t => t.Title ?? $"Track {t.Number:00}");
                Selected = Songs.Select(t => t.Number).ToHashSet();
                // names saved for this image last time (or written by hand) load automatically
                if (File.Exists(NamesFile))
                    try { NamesNote = $"{LoadNames(NamesFile)} name(s) loaded from {System.IO.Path.GetFileName(NamesFile)}"; } catch { }
                if (Songs.Count == Nrg.Tracks.Count)
                {
                    var first = Songs[0];
                    int Frame(long bytes) => (int)((bytes - first.Start) / first.SectorSize + 150);
                    Toc = new Toc(!Nrg.IsRawAudio, 1, Songs.Count, Frame(Songs[^1].End), Songs.Select(t => Frame(t.Start)).ToArray());
                }
            }
            catch (Exception ex) { Error = ex.Message; }
            Loading = false;
        }
    }

    static partial class Audio
    {
        const int Sector = 2352;

        /// <summary>A song's audio as CD PCM (44.1 kHz / 16-bit / stereo, little-endian): subchannel bytes dropped, byte order fixed.</summary>
        public static byte[] ReadSong(string dataPath, Nrg.Track t, bool swap)
        {
            var pcm = new byte[t.Sectors * Sector];
            var buf = new byte[t.SectorSize * 256];
            using var f = File.OpenRead(dataPath);
            f.Seek(t.Start, SeekOrigin.Begin);
            long done = 0;
            for (long left = t.Sectors; left > 0;)
            {
                int n = (int)Math.Min(256, left);
                Program.ReadExactly(f, buf, n * t.SectorSize);
                for (int s = 0; s < n; s++)
                {
                    Buffer.BlockCopy(buf, s * t.SectorSize, pcm, (int)(done * Sector), Sector);
                    done++;
                }
                left -= n;
            }
            if (swap)
                for (int i = 0; i + 1 < pcm.Length; i += 2) (pcm[i], pcm[i + 1]) = (pcm[i + 1], pcm[i]);
            return pcm;
        }
    
        /// <summary>Several songs of a disc as one: neighbouring songs keep the gap between them, others are joined end to end.</summary>
        public static byte[] CombineSongs(DiscItem d, List<Nrg.Track> chosen)
        {
            using var ms = new MemoryStream();
            for (int i = 0; i < chosen.Count; i++)
            {
                var t = chosen[i];
                int at = d.Songs.IndexOf(t);
                bool neighbour = i + 1 < chosen.Count && at + 1 < d.Songs.Count && d.Songs[at + 1] == chosen[i + 1];
                // run on to where the next song starts, keeping the gap between them
                var piece = new Nrg.Track { Number = t.Number, SectorSize = t.SectorSize, Mode = t.Mode, Start = t.Start, End = neighbour ? chosen[i + 1].Start : t.End };
                var pcm = ReadSong(d.Nrg.DataPath, piece, d.Swap);
                ms.Write(pcm, 0, pcm.Length);
            }
            return ms.ToArray();
        }
    }
}
