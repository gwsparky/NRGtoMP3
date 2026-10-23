using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace NrgToMp3
{
    /// <summary>
    /// Reads the track list and CD-Text out of a Nero image. The track data sits at the start of the file;
    /// a list of chunks (CUEX, DAOX, CDTX, ..., END!) at the end says where each track is.
    /// </summary>
    class Nrg
    {
        public class Track
        {
            public int Number;
            public int SectorSize;      // 2352 for audio, 2448 if subchannel data was saved too
            public int Mode;            // Nero's track mode; 7 = audio
            public long Start, End;     // byte offsets of index 1 and the end of the track
            public long PreGap = -1;    // byte offset of index 0 (start of the gap before the song), if known
            public double GapSeconds => PreGap >= 0 && PreGap < Start ? (Start - PreGap) / SectorSize / 75.0 : 0;
            public string Title, Performer;
            public bool IsAudio => Mode == 7 && SectorSize >= 2352;
            public long Sectors => (End - Start) / SectorSize;
        }

        public List<Track> Tracks = new List<Track>();
        public string Album, Performer;
        public bool HasCdText;
        public bool IsRawAudio;   // no Nero track list; songs were found by looking for silence
        public string DataPath;   // file holding the audio: the image itself, or a .cue sheet's .bin

        const int RawSector = 2352;
        const int SilencePeak = 200;            // loudest sample (of 32767) still counted as silence
        const int MinGapSectors = 5 * 75;       // a pause this long (5 s) separates two songs
        const int MinSongSectors = 10 * 75;     // ignore "songs" shorter than 10 s

        public static Nrg Read(string path)
        {
            var nrg = Path.GetExtension(path).ToLowerInvariant() == ".cue" ? ReadCue(path) : ReadNrg(path);
            nrg.DataPath ??= path;
            return nrg;
        }

        /// <summary>
        /// CUE sheet + raw BIN (the format the app's "Copy CD" saves). Audio tracks only; INDEX 00 marks the gap before a song.
        /// </summary>
        static Nrg ReadCue(string path)
        {
            var nrg = new Nrg();
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            Track cur = null;
            long Bytes(string msf)
            {
                var p = msf.Split(':').Select(int.Parse).ToArray();
                return ((long)p[0] * 60 * 75 + p[1] * 75 + p[2]) * RawSector;
            }
            string Arg(string line)
            {
                int q = line.IndexOf('"');
                if (q >= 0) { int e = line.LastIndexOf('"'); return e > q ? line.Substring(q + 1, e - q - 1) : line.Substring(q + 1); }
                var parts = line.Split((char[])null, 2, StringSplitOptions.RemoveEmptyEntries);
                return parts.Length > 1 ? parts[1].Trim() : "";
            }
            foreach (var raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                string word = line.Split(' ')[0].ToUpperInvariant();
                switch (word)
                {
                    case "FILE":
                        if (nrg.DataPath != null) throw new InvalidDataException("CUE sheets with more than one FILE aren't supported.");
                        string name = Arg(line.Substring(0, line.LastIndexOf(' ') > line.LastIndexOf('"') ? line.LastIndexOf(' ') : line.Length));
                        nrg.DataPath = Path.Combine(dir, name);
                        if (!File.Exists(nrg.DataPath)) throw new FileNotFoundException($"The CUE sheet's audio file is missing: {name}");
                        if (!line.ToUpperInvariant().EndsWith("BINARY")) throw new InvalidDataException("Only BINARY (raw) CUE images are supported.");
                        break;
                    case "TRACK":
                        var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        cur = new Track { Number = nrg.Tracks.Count + 1, SectorSize = RawSector, Mode = t.Length > 2 && t[2].ToUpperInvariant() == "AUDIO" ? 7 : 0 };
                        nrg.Tracks.Add(cur);
                        break;
                    case "TITLE":
                        if (cur == null) nrg.Album = Arg(line); else cur.Title = Arg(line);
                        break;
                    case "PERFORMER":
                        if (cur == null) nrg.Performer = Arg(line); else cur.Performer = Arg(line);
                        break;
                    case "INDEX":
                        var ix = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (cur == null || ix.Length < 3) break;
                        if (ix[1] == "00" || ix[1] == "0") cur.PreGap = Bytes(ix[2]);
                        else if (ix[1] == "01" || ix[1] == "1") cur.Start = Bytes(ix[2]);
                        break;
                }
            }
            if (nrg.DataPath == null || nrg.Tracks.Count == 0) throw new InvalidDataException("This CUE sheet doesn't list any tracks.");
            long length = new FileInfo(nrg.DataPath).Length;
            for (int i = 0; i < nrg.Tracks.Count; i++)   // a song ends where the next one's gap (or the next song) begins
            {
                var next = i + 1 < nrg.Tracks.Count ? nrg.Tracks[i + 1] : null;
                nrg.Tracks[i].End = next == null ? length - length % RawSector : (next.PreGap >= 0 ? next.PreGap : next.Start);
            }
            nrg.HasCdText = nrg.Tracks.Any(t => t.Title != null);
            return nrg;
        }

        static Nrg ReadNrg(string path)
        {
            using (var f = File.OpenRead(path))
            {
                var tail = new byte[12];
                f.Seek(-12, SeekOrigin.End);
                f.Read(tail, 0, 12);
                long chunks;   // NER5 (Nero 5.5 and later) uses a 64-bit offset; the older NERO format a 32-bit one
                if (Ascii(tail, 0) == "NER5") chunks = (long)BE64(tail, 4);
                else if (Ascii(tail, 4) == "NERO") chunks = BE32(tail, 8);
                else if (f.Length % RawSector == 0 && f.Length > 0) return ReadRawAudio(f);
                else throw new InvalidDataException("This isn't a Nero .nrg image (no Nero footer at the end of the file).");

                if (chunks <= 0 || chunks >= f.Length) throw new InvalidDataException("The image's track list is damaged.");
                var buf = new byte[f.Length - chunks];
                f.Seek(chunks, SeekOrigin.Begin);
                f.Read(buf, 0, buf.Length);

                var nrg = new Nrg();
                byte[] cdText = null;
                for (int p = 0; p + 8 <= buf.Length;)
                {
                    string id = Ascii(buf, p);
                    int size = (int)BE32(buf, p + 4), body = p + 8;
                    if (id == "END!" || body + size > buf.Length) break;
                    switch (id)
                    {
                        case "DAOX": case "DAOI": nrg.ReadDao(buf, body, id == "DAOX"); break;
                        case "ETN2": case "ETNF": nrg.ReadTao(buf, body, size, id == "ETN2"); break;
                        case "CDTX": cdText = buf.Skip(body).Take(size).ToArray(); break;
                    }
                    p = body + size;
                }
                if (nrg.Tracks.Count == 0) throw new InvalidDataException("Couldn't find any tracks in the image.");
                if (cdText != null) nrg.ApplyCdText(cdText);
                return nrg;
            }
        }

        /// <summary>
        /// Some "images" are just the disc's raw audio with the Nero track list missing. If it looks like CD audio,
        /// split it into songs wherever there's a long enough silence.
        /// </summary>
        static Nrg ReadRawAudio(FileStream f)
        {
            long sectors = f.Length / RawSector;
            var quiet = new bool[sectors];
            var buf = new byte[RawSector * 256];
            f.Seek(0, SeekOrigin.Begin);
            double little = 0, big = 0;
            for (long s = 0; s < sectors;)
            {
                int n = (int)Math.Min(256, sectors - s), got = 0;
                while (got < n * RawSector) { int r = f.Read(buf, got, n * RawSector - got); if (r == 0) break; got += r; }
                for (int k = 0; k < n; k++)
                {
                    int o = k * RawSector, peak = 0;
                    for (int i = o; i < o + RawSector; i += 2) peak = Math.Max(peak, Math.Abs((int)(short)(buf[i] | buf[i + 1] << 8)));
                    quiet[s + k] = peak < SilencePeak;
                    if (s + k < 2000) continue;   // byte-order check on a sample from past the intro
                    if ((s + k) % 50 == 0)
                        for (int i = o + 4; i < o + RawSector; i += 2)
                        {
                            little += Math.Abs((short)(buf[i] | buf[i + 1] << 8) - (short)(buf[i - 4] | buf[i - 3] << 8));
                            big += Math.Abs((short)(buf[i] << 8 | buf[i + 1]) - (short)(buf[i - 4] << 8 | buf[i - 3]));
                        }
                }
                s += n;
            }
            // Real audio changes smoothly in one byte order or the other; data looks like noise in both.
            if (Math.Min(little, big) * 4 > Math.Max(little, big))
                throw new InvalidDataException("This isn't a Nero .nrg image, and it doesn't look like raw CD audio either.");

            var nrg = new Nrg { IsRawAudio = true };
            long songStart = -1;
            for (long s = 0; s <= sectors; s++)
            {
                if (s < sectors && !quiet[s]) { if (songStart < 0) songStart = s; continue; }
                long gapEnd = s;
                while (gapEnd < sectors && quiet[gapEnd]) gapEnd++;
                if (songStart >= 0 && (gapEnd - s >= MinGapSectors || gapEnd == sectors))
                {
                    if (s - songStart >= MinSongSectors)
                        nrg.Tracks.Add(new Track { Number = nrg.Tracks.Count + 1, SectorSize = RawSector, Mode = 7,
                                                   Start = songStart * RawSector, End = s * RawSector });
                    songStart = -1;
                }
                s = gapEnd;
                if (s < sectors && songStart < 0) songStart = s;
            }
            if (nrg.Tracks.Count == 0) throw new InvalidDataException("Couldn't find any songs in the raw audio.");
            return nrg;
        }

        /// <summary>Disc-at-once session: 22-byte header (size, 14-byte UPC, TOC type, first/last track), then one entry per track.</summary>
        void ReadDao(byte[] b, int body, bool wide)
        {
            int first = b[body + 20], last = b[body + 21], entry = wide ? 42 : 30;
            for (int n = first; n <= last; n++)
            {
                int q = body + 22 + (n - first) * entry;   // 12-byte ISRC, sector size, mode, 3 unknown bytes, then pregap/index 1/end offsets
                var t = new Track { Number = Tracks.Count + 1, SectorSize = b[q + 12] << 8 | b[q + 13], Mode = b[q + 14] };
                if (wide) { t.PreGap = (long)BE64(b, q + 18); t.Start = (long)BE64(b, q + 26); t.End = (long)BE64(b, q + 34); }
                else { t.PreGap = BE32(b, q + 18); t.Start = BE32(b, q + 22); t.End = BE32(b, q + 26); }
                Tracks.Add(t);
            }
        }

        /// <summary>Track-at-once session: one entry per track (offset, length, mode, ...).</summary>
        void ReadTao(byte[] b, int body, int size, bool wide)
        {
            int entry = wide ? 32 : 20;
            for (int q = body; q + entry <= body + size; q += entry)
            {
                long offset = wide ? (long)BE64(b, q) : BE32(b, q), length = wide ? (long)BE64(b, q + 8) : BE32(b, q + 4);
                int mode = (int)BE32(b, q + (wide ? 16 : 8));
                int sectorSize = mode == 7 ? 2352 : mode == 0 ? 2048 : 2336;
                Tracks.Add(new Track { Number = Tracks.Count + 1, SectorSize = sectorSize, Mode = mode, Start = offset, End = offset + length });
            }
        }

        /// <summary>CD-Text packs are 18 bytes; their 12 text bytes hold NUL-separated strings, one per track, starting at the pack's track. Track 0 is the whole album.</summary>
        void ApplyCdText(byte[] packs)
        {
            var titles = CdTextStrings(packs, 0x80);
            var performers = CdTextStrings(packs, 0x81);
            foreach (var t in Tracks)
            {
                if (titles.TryGetValue(t.Number, out var title) && title != "") t.Title = title;
                // Some burning programs copy the song title into the performer field; that's not an artist.
                if (performers.TryGetValue(t.Number, out var who) && who != "" && who != t.Title) t.Performer = who;
            }
            titles.TryGetValue(0, out var album);
            performers.TryGetValue(0, out var artist);
            Album = string.IsNullOrEmpty(album) ? null : album;
            Performer = string.IsNullOrEmpty(artist) ? null : artist;
            HasCdText = Tracks.Any(t => t.Title != null);
        }

        static Dictionary<int, string> CdTextStrings(byte[] buf, byte type)
        {
            var result = new Dictionary<int, string>();
            var latin1 = Encoding.GetEncoding(28591);
            var cur = new List<byte>();
            int track = -1;
            for (int p = 0; p + 18 <= buf.Length; p += 18)
            {
                if (buf[p] != type || (buf[p + 3] & 0xF0) != 0) continue;   // first language block, single-byte text only
                if (track < 0) track = buf[p + 1] & 0x7F;
                for (int i = 4; i < 16; i++)
                {
                    if (buf[p + i] != 0) { cur.Add(buf[p + i]); continue; }
                    string s = latin1.GetString(cur.ToArray());
                    cur.Clear();
                    if (s == "\t") result.TryGetValue(track - 1, out s);   // tab = same as previous track
                    result[track++] = (s ?? "").Trim();
                }
            }
            return result;
        }

        static string Ascii(byte[] b, int o) => Encoding.ASCII.GetString(b, o, 4);
        static uint BE32(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
        static ulong BE64(byte[] b, int o) => (ulong)BE32(b, o) << 32 | BE32(b, o + 4);
    }
}
