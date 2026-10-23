using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace NrgToMp3
{
    /// <summary>Unpacks the songs of Nero (.nrg) music-CD images into a folder of MP3s next to each image.
    /// The macOS app runs this as a command-line tool; the Windows app calls Unpack directly.</summary>
    static class Program
    {
        internal const int Mp3BitRateKbps = 256;
        internal const int CdSectorBytes = 2352;   // one sector of CD audio: 1/75 s of 44.1 kHz / 16-bit / stereo

        internal static string encoder, encoderPath;   // "mp3enc" (bundled with the app), "ffmpeg" or "lame"
        internal static string outputDir;              // -o <folder>; default is next to each image
        internal static bool wav;                      // --wav: write uncompressed WAV instead of MP3
        internal static HashSet<int> onlyTracks;       // --tracks 1,3,5: export just these track numbers
        internal static bool merge;                    // --merge: join the songs into one file per image
        internal static Meta meta;                     // --meta names.json: album/artist/song names chosen in the app
        internal static Action<string, string> Mp3Encoder;   // set by the Windows app: encode (pcmPath, mp3Path) with Windows' own encoder

        internal class Meta
        {
            public string album { get; set; }
            public string artist { get; set; }
            public Dictionary<string, string> titles { get; set; }
            public bool numbered { get; set; }   // titles already start with the track number ("07 - ...")
            public string mergeName { get; set; } // --merge: name for the merged file (default: the album)
        }

        internal static int Main(string[] args)
        {
            var images = new List<string>();
            var inputs = args.Length > 0 ? args.ToList() : Prompt();
            int o = inputs.FindIndex(x => x == "-o" || x == "--out");
            if (o >= 0 && o + 1 < inputs.Count)
            {
                outputDir = Path.GetFullPath(CleanPath(inputs[o + 1]));
                inputs.RemoveRange(o, 2);
                Directory.CreateDirectory(outputDir);
            }
            wav = inputs.RemoveAll(x => x == "--wav") > 0;
            bool list = inputs.RemoveAll(x => x == "--list") > 0;
            merge = inputs.RemoveAll(x => x == "--merge") > 0;
            int mi = inputs.FindIndex(x => x == "--meta");
            if (mi >= 0 && mi + 1 < inputs.Count)
            {
                meta = System.Text.Json.JsonSerializer.Deserialize<Meta>(File.ReadAllText(inputs[mi + 1]));
                inputs.RemoveRange(mi, 2);
            }
            int ti = inputs.FindIndex(x => x == "--tracks");
            if (ti >= 0 && ti + 1 < inputs.Count)
            {
                onlyTracks = inputs[ti + 1].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();
                inputs.RemoveRange(ti, 2);
            }
            foreach (var raw in inputs)
            {
                string a = CleanPath(raw);
                if (a == "") continue;
                if (Directory.Exists(a)) images.AddRange(Directory.GetFiles(a, "*.nrg").Concat(Directory.GetFiles(a, "*.cue")).OrderBy(f => f));
                else if (File.Exists(a)) images.Add(a);
                else Console.WriteLine($"Not found: {a}");
            }
            if (images.Count == 0)
            {
                Console.WriteLine("No .nrg files given. Usage: NrgToMp3 [--list] [--wav] [--merge] [--tracks 1,2,...] [--meta names.json] [-o output-folder] <file.nrg | folder> ...");
                return 1;
            }

            if (list) { foreach (var img in images) ListTracks(img); return 0; }

            if (wav) { }   // WAV is written directly; no encoder needed
            else if (File.Exists(encoderPath = Path.Combine(AppContext.BaseDirectory, "mp3enc"))) encoder = "mp3enc";
            else if ((encoderPath = FindTool("ffmpeg")) != null) encoder = "ffmpeg";
            else if ((encoderPath = FindTool("lame")) != null) encoder = "lame";
            else
            {
                Console.WriteLine("Couldn't find an MP3 encoder. Install one with Homebrew:  brew install ffmpeg");
                return 1;
            }

            int failed = 0;
            foreach (var img in images)
            {
                try { Unpack(img); }
                catch (Exception ex) { failed++; Console.WriteLine($"  FAILED: {ex.Message}"); }
            }
            Console.WriteLine();
            Console.WriteLine(failed == 0 ? $"Done: {images.Count} image(s) unpacked." : $"Finished with {failed} of {images.Count} image(s) failing.");
            return failed;
        }

        /// <summary>No arguments: ask for paths. Dragging files from Finder into Terminal pastes their paths.</summary>
        internal static List<string> Prompt()
        {
            Console.WriteLine("Drag .nrg files (or a folder of them) into this window, then press Enter:");
            var line = Console.ReadLine() ?? "";
            var list = new List<string>();
            var cur = new System.Text.StringBuilder();
            char quote = '\0';
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '\\' && i + 1 < line.Length && quote == '\0') cur.Append(line[++i]);   // Terminal escapes spaces as "\ "
                else if (quote != '\0') { if (c == quote) quote = '\0'; else cur.Append(c); }
                else if (c == '\'' || c == '"') quote = c;
                else if (c == ' ') { if (cur.Length > 0) { list.Add(cur.ToString()); cur.Clear(); } }
                else cur.Append(c);
            }
            if (cur.Length > 0) list.Add(cur.ToString());
            return list;
        }

        internal static string CleanPath(string p)
        {
            p = p.Trim();
            if (p.StartsWith("~/")) p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p.Substring(2));
            return p;
        }

        internal static string FindTool(string name)
        {
            var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Concat(new[] { "/opt/homebrew/bin", "/usr/local/bin", "/opt/local/bin" });   // Finder-launched apps don't always see Homebrew on PATH
            foreach (var d in dirs)
            {
                var full = Path.Combine(d, name);
                if (File.Exists(full)) return full;
            }
            return null;
        }

        /// <summary>
        /// --list: one JSON line per image describing its songs and where their audio sits in the file,
        /// so the app can preview them without converting anything.
        /// </summary>
        internal static void ListTracks(string nrgPath)
        {
            object result;
            try
            {
                var nrg = Nrg.Read(nrgPath);
                var songs = nrg.Tracks.Where(t => t.IsAudio).ToList();
                if (songs.Count == 0) throw new InvalidDataException("This image has no music tracks (it's a data disc).");
                bool swap;
                using (var image = File.OpenRead(nrg.DataPath)) swap = IsBigEndian(image, songs[0]);
                result = new
                {
                    path = nrgPath,
                    dataPath = nrg.DataPath,
                    artist = nrg.Performer,
                    album = nrg.Album ?? Path.GetFileNameWithoutExtension(nrgPath),
                    note = nrg.IsRawAudio ? "No Nero track list in this file; songs were split at silences." : null,
                    swap,
                    toc = Toc(nrg, songs),
                    tracks = songs.Select(t => new
                    {
                        number = t.Number,
                        title = string.IsNullOrEmpty(t.Title) ? $"Track {t.Number:00}" : t.Title,
                        performer = t.Performer ?? nrg.Performer,
                        seconds = t.Sectors / 75.0,
                        start = t.Start,
                        sectors = t.Sectors,
                        sectorSize = t.SectorSize,
                        gap = t.GapSeconds,
                    }),
                };
            }
            catch (Exception ex) { result = new { path = nrgPath, error = ex.Message }; }
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
        }

        /// <summary>
        /// The disc's table of contents in CD frames (MusicBrainz style: first track at 150), for looking up song names online.
        /// Exact for real Nero images; only approximate for raw audio, where song starts were guessed from silence.
        /// </summary>
        internal static object Toc(Nrg nrg, List<Nrg.Track> songs)
        {
            if (songs.Count != nrg.Tracks.Count) return null;   // mixed audio/data discs: skip
            var first = songs[0];   // the first song starts at frame 150, after the standard 2 s lead-in
            long Frame(long bytes) => (bytes - first.Start) / first.SectorSize + 150;
            return new
            {
                exact = !nrg.IsRawAudio,
                first = 1,
                last = songs.Count,
                leadout = Frame(songs[^1].End),
                offsets = songs.Select(t => Frame(t.Start)).ToArray(),
            };
        }

        internal static void Unpack(string nrgPath)
        {
            Console.WriteLine();
            Console.WriteLine(Path.GetFileName(nrgPath));
            var nrg = Nrg.Read(nrgPath);
            var songs = nrg.Tracks.Where(t => t.IsAudio).ToList();
            if (songs.Count == 0) throw new InvalidDataException("This image has no music tracks (it's a data disc).");
            if (meta != null)   // names typed or looked up in the app replace the ones from the disc
            {
                if (!string.IsNullOrWhiteSpace(meta.album)) nrg.Album = meta.album.Trim();
                if (meta.artist != null) { nrg.Performer = meta.artist.Trim() == "" ? null : meta.artist.Trim(); foreach (var t in songs) t.Performer = null; }
                foreach (var t in songs)
                    if (meta.titles != null && meta.titles.TryGetValue(t.Number.ToString(), out var name) && !string.IsNullOrWhiteSpace(name))
                        t.Title = name.Trim();
            }
            int totalSongs = songs.Count;
            var allSongs = songs;
            if (onlyTracks != null) songs = songs.Where(t => onlyTracks.Contains(t.Number)).ToList();
            if (songs.Count == 0) throw new InvalidDataException("None of the chosen tracks are in this image.");

            string imageName = Path.GetFileNameWithoutExtension(nrgPath);
            string album = string.IsNullOrEmpty(nrg.Album) ? imageName : nrg.Album;
            string parent = outputDir ?? Path.GetDirectoryName(Path.GetFullPath(nrgPath));
            if (merge) { Merge(nrg, allSongs, songs, album, parent); return; }
            string folder = Path.Combine(parent, Clean(imageName));
            for (int k = 2; Directory.Exists(folder); k++) folder = Path.Combine(parent, Clean($"{imageName} ({k})"));
            Directory.CreateDirectory(folder);

            using (var image = File.OpenRead(nrg.DataPath))
            {
                bool swap = IsBigEndian(image, songs[0]);
                Console.WriteLine($"  {songs.Count} song(s){(nrg.IsRawAudio ? " found by silence (no Nero track list in file)" : nrg.HasCdText ? ", names from CD-Text" : ", no names on the disc")} -> {folder}");
                string temp = Path.Combine(Path.GetTempPath(), $"nrg2mp3-{Guid.NewGuid():N}");
                Directory.CreateDirectory(temp);
                try
                {
                    foreach (var t in songs)
                    {
                        string ext = wav ? ".wav" : ".mp3";
                        string fileName = string.IsNullOrEmpty(t.Title) ? $"Track {t.Number:00}{ext}"
                            : meta?.numbered == true ? Clean(t.Title) + ext          // number is already in the name
                            : Clean($"{t.Number:00} - {t.Title}") + ext;
                        Console.Write($"  {fileName} ... ");
                        string pcm = Path.Combine(temp, "track.pcm"), untagged = Path.Combine(temp, "track.mp3");
                        ExtractPcm(image, t, swap, pcm);
                        if (wav)
                        {
                            Wav.Write(pcm, Path.Combine(folder, fileName), string.IsNullOrEmpty(t.Title) ? $"Track {t.Number:00}" : t.Title,
                                      t.Performer ?? nrg.Performer, album, t.Number.ToString());
                            File.Delete(pcm);
                            Console.WriteLine(TimeSpan.FromSeconds(t.Sectors / 75.0).ToString(@"m\:ss"));
                            continue;
                        }
                        EncodeMp3(pcm, untagged);
                        var tags = new List<(string, string)>
                        {
                            ("TIT2", string.IsNullOrEmpty(t.Title) ? $"Track {t.Number:00}" : t.Title),
                            ("TRCK", $"{t.Number}/{totalSongs}"),
                            ("TPE1", t.Performer ?? nrg.Performer),
                            ("TPE2", nrg.Performer),
                            ("TALB", album),
                        };
                        Id3.WriteTagged(untagged, Path.Combine(folder, fileName), tags.Where(x => !string.IsNullOrEmpty(x.Item2)).ToList());
                        File.Delete(pcm); File.Delete(untagged);
                        Console.WriteLine(TimeSpan.FromSeconds(t.Sectors / 75.0).ToString(@"m\:ss"));
                    }
                }
                catch
                {
                    try { Directory.Delete(folder, true); } catch { }   // don't leave half an album behind
                    throw;
                }
                finally { try { Directory.Delete(temp, true); } catch { } }
            }
        }

        /// <summary>
        /// --merge: joins the chosen songs into one file named after the album, saved straight into the output folder.
        /// Where two chosen songs are next to each other on the disc, the gap between them is kept, so the file sounds
        /// like playing the CD straight through; songs with skipped songs between them are joined end to end.
        /// </summary>
        internal static void Merge(Nrg nrg, List<Nrg.Track> allSongs, List<Nrg.Track> songs, string album, string parent)
        {
            string ext = wav ? ".wav" : ".mp3";
            string name = string.IsNullOrWhiteSpace(meta?.mergeName) ? album : meta.mergeName.Trim();
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) name = name[..^ext.Length].TrimEnd();   // "Easter.mp3" typed in full
            if (Clean(name) == "") name = album;
            string file = Path.Combine(parent, Clean(name) + ext);
            for (int k = 2; File.Exists(file); k++) file = Path.Combine(parent, Clean($"{name} ({k})") + ext);
            Console.Write($"  {songs.Count} song(s) merged -> {file} ... ");

            string temp = Path.Combine(Path.GetTempPath(), $"nrg2mp3-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temp);
            try
            {
                string pcm = Path.Combine(temp, "merged.pcm"), part = Path.Combine(temp, "part.pcm"), untagged = Path.Combine(temp, "merged.mp3");
                long sectors = 0;
                using (var image = File.OpenRead(nrg.DataPath))
                using (var output = File.Create(pcm))
                {
                    bool swap = IsBigEndian(image, songs[0]);
                    for (int i = 0; i < songs.Count; i++)
                    {
                        var t = songs[i];
                        int at = allSongs.IndexOf(t);
                        bool nextIsNeighbour = i + 1 < songs.Count && at + 1 < allSongs.Count && allSongs[at + 1] == songs[i + 1];
                        // run on to where the next song starts, to keep the gap between them
                        var piece = new Nrg.Track { Number = t.Number, SectorSize = t.SectorSize, Mode = t.Mode, Start = t.Start,
                                                    End = nextIsNeighbour ? songs[i + 1].Start : t.End };
                        ExtractPcm(image, piece, swap, part);
                        using (var p = File.OpenRead(part)) p.CopyTo(output);
                        sectors += piece.Sectors;
                    }
                }
                string artist = nrg.Performer ?? songs.Select(t => t.Performer).FirstOrDefault(x => !string.IsNullOrEmpty(x));
                if (wav) Wav.Write(pcm, file, name, artist, album, null);
                else
                {
                    EncodeMp3(pcm, untagged);
                    var tags = new List<(string, string)> { ("TIT2", name), ("TPE1", artist), ("TPE2", artist), ("TALB", album) };
                    Id3.WriteTagged(untagged, file, tags.Where(x => !string.IsNullOrEmpty(x.Item2)).ToList());
                }
                Console.WriteLine(TimeSpan.FromSeconds(sectors / 75.0).ToString(sectors >= 75 * 3600 ? @"h\:mm\:ss" : @"m\:ss"));
            }
            catch
            {
                try { File.Delete(file); } catch { }   // don't leave half a file behind
                throw;
            }
            finally { try { Directory.Delete(temp, true); } catch { } }
        }

        /// <summary>Encodes raw 44.1 kHz / 16-bit / stereo little-endian PCM to MP3 with ffmpeg or lame.</summary>
        internal static void EncodeMp3(string pcmPath, string mp3Path)
        {
            if (Mp3Encoder != null) { Mp3Encoder(pcmPath, mp3Path); return; }
            var psi = new ProcessStartInfo(encoderPath) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            if (encoder == "mp3enc")
            {
                foreach (var a in new[] { pcmPath, mp3Path, Mp3BitRateKbps.ToString() }) psi.ArgumentList.Add(a);
            }
            else if (encoder == "ffmpeg")
            {
                foreach (var a in new[] { "-y", "-loglevel", "error", "-f", "s16le", "-ar", "44100", "-ac", "2", "-i", pcmPath,
                                          "-map_metadata", "-1", "-write_id3v2", "0", "-codec:a", "libmp3lame", "-b:a", Mp3BitRateKbps + "k", mp3Path })
                    psi.ArgumentList.Add(a);
            }
            else
            {
                foreach (var a in new[] { "--silent", "-r", "-s", "44.1", "--signed", "--little-endian", "-b", Mp3BitRateKbps.ToString(), pcmPath, mp3Path })
                    psi.ArgumentList.Add(a);
            }
            using (var p = Process.Start(psi))
            {
                var err = p.StandardError.ReadToEndAsync();
                p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0 || !File.Exists(mp3Path))
                    throw new InvalidOperationException($"{encoder} failed: {err.Result.Trim()}");
            }
        }

        /// <summary>Copies a track's audio (index 1 to end, no pregap) out as little-endian PCM, dropping any subchannel bytes.</summary>
        internal static void ExtractPcm(FileStream image, Nrg.Track t, bool swap, string pcmPath)
        {
            var buf = new byte[t.SectorSize * 64];
            image.Seek(t.Start, SeekOrigin.Begin);
            using (var outFile = File.Create(pcmPath))
            {
                for (long left = t.Sectors; left > 0;)
                {
                    int n = (int)Math.Min(64, left);
                    ReadExactly(image, buf, n * t.SectorSize);
                    for (int s = 0; s < n; s++)
                    {
                        int o = s * t.SectorSize;
                        if (swap) for (int i = o; i < o + CdSectorBytes; i += 2) { byte x = buf[i]; buf[i] = buf[i + 1]; buf[i + 1] = x; }
                        outFile.Write(buf, o, CdSectorBytes);
                    }
                    left -= n;
                }
            }
        }

        /// <summary>
        /// Nero has stored audio in both byte orders over the years. Real music changes smoothly from one sample to the next,
        /// so whichever byte order gives the smaller jumps between neighbouring samples is the right one.
        /// </summary>
        internal static bool IsBigEndian(FileStream image, Nrg.Track t)
        {
            int sectors = (int)Math.Min(t.Sectors, 300);
            var buf = new byte[sectors * t.SectorSize];
            image.Seek(t.Start + Math.Max(0, t.Sectors / 2 - sectors / 2) * t.SectorSize, SeekOrigin.Begin);
            ReadExactly(image, buf, buf.Length);
            double little = 0, big = 0;
            for (int s = 0; s < sectors; s++)
                for (int i = s * t.SectorSize + 4; i + 1 < s * t.SectorSize + CdSectorBytes; i += 2)
                {
                    little += Math.Abs((short)(buf[i] | buf[i + 1] << 8) - (short)(buf[i - 4] | buf[i - 3] << 8));   // same channel, one sample back
                    big += Math.Abs((short)(buf[i] << 8 | buf[i + 1]) - (short)(buf[i - 4] << 8 | buf[i - 3]));
                }
            return big < little;
        }

        internal static void ReadExactly(Stream s, byte[] buf, int count)
        {
            for (int got = 0; got < count;)
            {
                int n = s.Read(buf, got, count - got);
                if (n == 0) throw new EndOfStreamException("The image file is cut short (incomplete download or copy?).");
                got += n;
            }
        }

        /// <summary>A file name that works on both Windows and macOS (files often move between the two).</summary>
        internal static string Clean(string s)
        {
            var bad = Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*").ToArray();
            s = new string(s.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.', ' ');
            return s.Length > 100 ? s.Substring(0, 100).TrimEnd('.', ' ') : s;
        }
    }
}
