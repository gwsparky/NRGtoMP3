using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NrgToMp3;
using SoundTouch;

namespace NrgToMp3Win
{
    /// <summary>Song-level tools shared by the Split Song and Change Key tabs. Nothing here is Windows-specific.
    /// Audio is always CD format: 44.1 kHz, 16-bit, stereo, little-endian PCM (4 bytes per frame).</summary>
    static class SongCore
    {
        public const int BytesPerSecond = 176_400;
        public const int SectorBytes = 2352;           // 1/75 s: CD tracks can only start on these steps

        public static double Seconds(byte[] pcm) => pcm.Length / (double)BytesPerSecond;

        // ---------- times

        /// <summary>"1:23.45": minutes, seconds and hundredths.</summary>
        public static string Precise(double s)
        {
            int cs = (int)Math.Round(Math.Max(0, s) * 100);
            return $"{cs / 6000}:{cs / 100 % 60:00}.{cs % 100:00}";
        }

        public static string Short(double s) => $"{(int)s / 60}:{(int)s % 60:00}";

        /// <summary>Accepts "1:23.45", "83.45" or "1:23".</summary>
        public static double? ParseTime(string text)
        {
            var parts = (text ?? "").Trim().Split(':');
            if (parts.Length is < 1 or > 2) return null;
            if (!double.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var last)) return null;
            if (parts.Length == 1) return last;
            return int.TryParse(parts[0], out var m) ? m * 60 + last : null;
        }

        /// <summary>A typed or pasted list, one part per line: a time plus an optional name ("1:32 Verse 2",
        /// "3. Finale – 2:15", "Verse 2 (1:32)"). With <paramref name="lengths"/>, each time is how long that part lasts.</summary>
        public static List<(double start, string name)> ParseTimeList(string text, bool lengths)
        {
            var parsed = new List<(double time, string name)>();
            foreach (var raw in (text ?? "").Split('\n'))
            {
                var line = raw.Trim();
                var m = Regex.Matches(line, @"\d+:\d{1,2}(\.\d+)?");
                if (m.Count == 0) m = Regex.Matches(line, @"\d+(\.\d+)?");
                if (m.Count == 0) continue;
                var hit = m[^1];
                if (ParseTime(hit.Value) is not double t) continue;
                var name = line.Remove(hit.Index, hit.Length).Insert(hit.Index, " ");
                name = Regex.Replace(name, @"^\s*\d{1,2}[.)]\s+", "");          // "3. " track numbers
                name = Regex.Replace(name, @"[(\[]\s*[)\]]", "");              // "Verse (1:32)" leaves "()"
                name = name.Trim(' ', '\t', '-', '–', '—', ':', '|');
                parsed.Add((t, name == "" ? null : name));
            }
            if (!lengths) return parsed;
            double at = 0;
            var starts = new List<(double, string)>();
            foreach (var (time, name) in parsed) { starts.Add((at, name)); at += time; }
            return starts;
        }

        // ---------- waveform and channels

        /// <summary>Loudest sample of each channel in each of <paramref name="count"/> slices, 0..1.</summary>
        public static (float[] left, float[] right) Peaks(byte[] pcm, int count = 1200)
        {
            var left = new float[count];
            var right = new float[count];
            int frames = pcm.Length / 4;
            if (frames == 0) return (left, right);
            int per = Math.Max(1, frames / count), step = Math.Max(1, per / 400);
            for (int b = 0; b < count; b++)
            {
                int l = 0, r = 0, end = Math.Min(frames, b * per + per);
                for (int f = b * per; f < end; f += step)
                {
                    l = Math.Max(l, Math.Abs((int)BitConverter.ToInt16(pcm, f * 4)));
                    r = Math.Max(r, Math.Abs((int)BitConverter.ToInt16(pcm, f * 4 + 2)));
                }
                left[b] = l / 32768f;
                right[b] = r / 32768f;
            }
            return (left, right);
        }

        public enum Channels { Both, RemoveLeft, RemoveRight }

        /// <summary>Removes one side; the empty side is either silent or a copy of the side that's kept.</summary>
        public static byte[] ApplyChannels(byte[] pcm, Channels mode, bool duplicate)
        {
            if (mode == Channels.Both) return pcm;
            var output = (byte[])pcm.Clone();
            int keep = mode == Channels.RemoveRight ? 0 : 2, drop = 2 - keep;   // byte offset of the kept / removed sample
            for (int f = 0; f + 3 < output.Length; f += 4)
            {
                output[f + drop] = duplicate ? output[f + keep] : (byte)0;
                output[f + drop + 1] = duplicate ? output[f + keep + 1] : (byte)0;
            }
            return output;
        }

        // ---------- key change

        /// <summary>Shifts the pitch by <paramref name="semitones"/> (fractions allowed) without changing the speed.
        /// The result has the same length as the input.</summary>
        public static byte[] ShiftPitch(byte[] pcm, double semitones, Action<double> progress = null)
        {
            if (Math.Abs(semitones) < 0.001) return pcm;
            var st = new SoundTouchProcessor { SampleRate = 44100, Channels = 2, PitchSemiTones = semitones, Tempo = 1, Rate = 1 };
            int frames = pcm.Length / 4;
            const int chunk = 8192;
            var input = new float[chunk * 2];
            var buf = new float[chunk * 2];
            var output = new byte[pcm.Length];
            int written = 0;                                     // frames written to output

            void Drain()
            {
                int n;
                while ((n = st.ReceiveSamples(buf, chunk)) > 0)
                    for (int fr = 0; fr < n && written < frames; fr++, written++)
                        for (int c = 0; c < 2; c++)
                        {
                            short v = (short)Math.Clamp(Math.Round(buf[fr * 2 + c] * 32768f), short.MinValue, short.MaxValue);
                            output[written * 4 + c * 2] = (byte)v;
                            output[written * 4 + c * 2 + 1] = (byte)(v >> 8);
                        }
            }

            for (int f = 0; f < frames; f += chunk)
            {
                int n = Math.Min(chunk, frames - f);
                for (int i = 0; i < n * 2; i++) input[i] = BitConverter.ToInt16(pcm, (f * 2 + i) * 2) / 32768f;
                st.PutSamples(input.AsSpan(0, n * 2), n);
                Drain();
                progress?.Invoke((double)(f + n) / frames);
            }
            st.Flush();
            Drain();
            return output;                                       // any frames not produced stay silent (only possible at the very end)
        }

        // ---------- saving

        /// <summary>Held while the converter runs: it keeps its settings in shared fields and writes progress to Console,
        /// so only one export (from any tab) runs at a time.</summary>
        public static readonly object ConsoleLock = new();

        /// <summary>Saves songs as MP3 or WAV files with names and tags, in a folder named after <paramref name="album"/>
        /// inside <paramref name="folder"/>, using the same converter as the Convert tab. Runs on the calling thread.</summary>
        public static void Export(IList<(string title, byte[] pcm)> songs, string album, string folder, bool wav, bool numberNames)
        {
            lock (ConsoleLock)
            {
                string temp = Path.Combine(Path.GetTempPath(), "nrg-export-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);
                try
                {
                    string safe = Program.Clean(album);
                    if (safe == "") safe = "Song";
                    string bin = Path.Combine(temp, safe + ".bin"), cue = Path.Combine(temp, safe + ".cue");
                    var text = new StringBuilder($"TITLE \"{album.Replace("\"", "'")}\"\r\nFILE \"{Path.GetFileName(bin)}\" BINARY\r\n");
                    using (var f = File.Create(bin))
                    {
                        long pos = 0;
                        for (int i = 0; i < songs.Count; i++)
                        {
                            text.Append($"  TRACK {i + 1:00} AUDIO\r\n    INDEX 01 {pos / 4500:00}:{pos / 75 % 60:00}:{pos % 75:00}\r\n");
                            var pcm = songs[i].pcm;
                            f.Write(pcm, 0, pcm.Length);
                            int pad = (SectorBytes - pcm.Length % SectorBytes) % SectorBytes;   // songs end on a whole sector
                            f.Write(new byte[pad], 0, pad);
                            pos += (pcm.Length + pad) / SectorBytes;
                        }
                    }
                    File.WriteAllText(cue, text.ToString());

                    bool numbered = numberNames && songs.Count > 1;
                    Program.outputDir = folder;
                    Directory.CreateDirectory(folder);
                    Program.wav = wav;
                    Program.merge = false;
                    Program.onlyTracks = null;
                    Program.meta = new Program.Meta
                    {
                        album = album,
                        numbered = numbered || songs.Count == 1,      // a single song needs no "01 - " in its file name
                        titles = songs.Select((s, i) => (i, s.title))
                                      .ToDictionary(x => (x.i + 1).ToString(), x => numbered ? $"{x.i + 1:00} - {x.title}" : x.title),
                    };
                    Program.Unpack(cue);
                }
                finally
                {
                    Program.meta = null;
                    try { Directory.Delete(temp, true); } catch { }
                }
            }
        }
    }

    /// <summary>A song being split: the split points (seconds, sorted) and a name for each part.</summary>
    class SplitPlan
    {
        public readonly string Title;
        public readonly byte[] Pcm;
        public readonly double Duration;
        public readonly List<double> Markers = new();
        public readonly List<string> Names = new() { null };   // one per part; null = automatic name
        public const double MinPart = 1.0;

        public SplitPlan(string title, byte[] pcm) { Title = title; Pcm = pcm; Duration = SongCore.Seconds(pcm); }

        public int Parts => Markers.Count + 1;
        public double Start(int k) => k == 0 ? 0 : Markers[k - 1];
        public double End(int k) => k == Markers.Count ? Duration : Markers[k];
        public string DefaultName(int k) => $"{Title} (Part {k + 1})";
        public string Name(int k) => string.IsNullOrWhiteSpace(Names[k]) ? DefaultName(k) : Names[k].Trim();

        static double Snap(double t) => Math.Round(t * 75) / 75;

        public bool Add(double t)
        {
            t = Snap(t);
            if (t < MinPart || t > Duration - MinPart || Markers.Any(m => Math.Abs(m - t) < MinPart)) return false;
            int i = Markers.FindIndex(m => m > t);
            if (i < 0) i = Markers.Count;
            Markers.Insert(i, t);
            Names.Insert(i + 1, null);            // the part being split keeps its name; the new one starts unnamed
            return true;
        }

        public void Move(int i, double t)
        {
            double lo = (i == 0 ? 0 : Markers[i - 1]) + MinPart, hi = (i == Markers.Count - 1 ? Duration : Markers[i + 1]) - MinPart;
            if (lo <= hi) Markers[i] = Snap(Math.Clamp(t, lo, hi));
        }

        /// <summary>Removing a split point joins the two parts around it; the first one's name is kept.</summary>
        public void Remove(int i) { Markers.RemoveAt(i); Names.RemoveAt(i + 1); }

        public (int added, List<double> skipped) Apply(List<(double start, string name)> entries, bool replace)
        {
            if (replace) { Markers.Clear(); Names.Clear(); Names.Add(null); }
            int added = 0;
            var skipped = new List<double>();
            foreach (var e in entries.OrderBy(e => e.start).Where(e => e.start > 0.001))
                if (Add(e.start)) added++; else skipped.Add(e.start);
            foreach (var e in entries.Where(e => !string.IsNullOrEmpty(e.name)))
            {
                int k = Enumerable.Range(0, Parts).OrderBy(k => Math.Abs(Start(k) - e.start)).First();
                if (Math.Abs(Start(k) - e.start) < 0.05) Names[k] = e.name;
            }
            return (added, skipped);
        }

        /// <summary>Each part's name and audio. Cuts fall on whole CD sectors, so the parts rejoin exactly.</summary>
        public List<(string title, byte[] pcm)> MakeParts()
        {
            var parts = new List<(string, byte[])>();
            for (int k = 0; k < Parts; k++)
            {
                long a = k == 0 ? 0 : Math.Min(Pcm.Length, (long)Math.Round(Start(k) * 75) * SongCore.SectorBytes);
                long b = k == Parts - 1 ? Pcm.Length : Math.Min(Pcm.Length, (long)Math.Round(End(k) * 75) * SongCore.SectorBytes);
                var slice = new byte[b - a];
                Buffer.BlockCopy(Pcm, (int)a, slice, 0, slice.Length);
                parts.Add((Name(k), slice));
            }
            return parts;
        }
    }
}
