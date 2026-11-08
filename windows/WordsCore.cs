using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace NrgToMp3Win
{
    enum WordsSide { Auto, Both, Left, Right }

    /// <summary>
    /// Song Words: writes down a song's words with Whisper (whisper.cpp, speech-to-text that runs on this PC; nothing is uploaded).
    /// Cross-platform apart from where the native files come from, so it can be tested on any OS.
    /// </summary>
    static class WordsCore
    {
        public const string ModelName = "ggml-small.en.bin";
        public const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en.bin";
        public static string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChurchSound");
        public static string ModelPath => Path.Combine(Folder, ModelName);
        static WhisperFactory factory;

        /// <summary>CD audio (44.1 kHz, 16-bit stereo) → 16 kHz mono samples (−1…1) from one side or both.</summary>
        public static float[] To16k(byte[] pcm, WordsSide side)
        {
            int frames = pcm.Length / 4;
            int outCount = (int)((long)frames * 16000 / 44100);
            var o = new float[outCount];
            for (int j = 0; j < outCount; j++)
            {
                int a = (int)((long)j * 44100 / 16000), b = Math.Min(frames, Math.Max(a + 1, (int)((long)(j + 1) * 44100 / 16000)));
                float sum = 0;
                for (int k = a; k < b; k++)
                {
                    float l = BitConverter.ToInt16(pcm, 4 * k), r = BitConverter.ToInt16(pcm, 4 * k + 2);
                    sum += side == WordsSide.Left ? l : side == WordsSide.Right ? r : (l + r) / 2;
                }
                o[j] = sum / (b - a) / 32768f;
            }
            return o;
        }

        /// <summary>True when the two sides carry different things (e.g. band on one side, singers on the other, as on split tracks).</summary>
        public static bool SidesDiffer(byte[] pcm)
        {
            double ll = 0, rr = 0, lr = 0;
            for (int i = 0; i + 3 < pcm.Length; i += 4 * 37)
            {
                double l = BitConverter.ToInt16(pcm, i), r = BitConverter.ToInt16(pcm, i + 2);
                ll += l * l; rr += r * r; lr += l * r;
            }
            if (ll == 0 || rr == 0) return ll != rr;
            return lr / Math.Sqrt(ll * rr) < 0.6;
        }

        public static int WordCount(string s) => s.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length;

        /// <summary>Trims lines and drops Whisper's sound labels like "(upbeat music)" or "[MUSIC]".</summary>
        public static string Clean(string line)
        {
            line = line.Trim();
            var core = line.Trim('(', ')', '[', ']', '♪', ' ');
            bool label = (line.StartsWith("(") && line.EndsWith(")")) || (line.StartsWith("[") && line.EndsWith("]"));
            return core.Length == 0 || label ? null : line;
        }

        /// <summary>Unpacks Whisper's Windows files (packed into the .exe) to %LOCALAPPDATA%\ChurchSound\whisper-1.8.1\runtimes\win-…\.</summary>
        static void Natives()
        {
            var asm = Assembly.GetExecutingAssembly();
            var names = asm.GetManifestResourceNames().Where(n => n.StartsWith("whisper/")).ToList();
            if (names.Count == 0) return;   // not the Windows build (e.g. a test on the Mac): Whisper.net finds its own files
            var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            var root = Path.Combine(Folder, "whisper-1.8.1");
            var dir = Path.Combine(root, "runtimes", "win-" + arch);
            Directory.CreateDirectory(dir);
            foreach (var n in names)
            {
                var dest = Path.Combine(dir, n.Substring("whisper/".Length));
                using var s = asm.GetManifestResourceStream(n);
                if (File.Exists(dest) && new FileInfo(dest).Length == s.Length) continue;
                using var f = File.Create(dest);
                s.CopyTo(f);
            }
            RuntimeOptions.LibraryPath = Path.Combine(root, "whisper.dll");   // only its folder is used, to find runtimes\win-…
            RuntimeOptions.RuntimeLibraryOrder = new() { RuntimeLibrary.Cpu };
        }

        /// <summary>Downloads the speech model (about 490 MB) the first time. Reports 0…1.</summary>
        public static async Task DownloadModel(IProgress<double> progress, CancellationToken cancel = default)
        {
            Directory.CreateDirectory(Folder);
            var tmp = ModelPath + ".part";
            using var http = new HttpClient { Timeout = TimeSpan.FromHours(2) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ChurchSound");
            using var resp = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, cancel);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? 0, done = 0;
            using (var src = await resp.Content.ReadAsStreamAsync(cancel))
            using (var dst = File.Create(tmp))
            {
                var buf = new byte[1 << 16];
                int n;
                while ((n = await src.ReadAsync(buf, cancel)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), cancel);
                    done += n;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }
            if (total > 0 && new FileInfo(tmp).Length != total) throw new IOException("The download stopped early. Try again.");
            File.Move(tmp, ModelPath, true);
        }

        /// <summary>Listens to 16 kHz samples and returns the words, one line per sung phrase.</summary>
        public static async Task<string> Transcribe(float[] samples, CancellationToken cancel = default)
        {
            if (factory == null) { Natives(); factory = WhisperFactory.FromPath(ModelPath); }
            // Beam search (like whisper-cli's default): the quicker "greedy" mode hears only "[MUSIC]" in songs.
            using var processor = factory.CreateBuilder().WithLanguage("en").WithBeamSearchSamplingStrategy().ParentBuilder.Build();
            var sb = new StringBuilder();
            await foreach (var seg in processor.ProcessAsync(samples, cancel))
                if (Clean(seg.Text) is string line) sb.AppendLine(line);
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Gets the words of a song. Automatic: if the sides differ, listens to each and keeps the one with more words.
        /// Returns the words and a note on which side they came from.
        /// </summary>
        public static async Task<(string words, string note)> GetWords(byte[] pcm, WordsSide side, Action<string> status, CancellationToken cancel = default)
        {
            if (side == WordsSide.Auto && SidesDiffer(pcm))
            {
                status("The two sides are different; listening to the left side…");
                var l = await Transcribe(To16k(pcm, WordsSide.Left), cancel);
                status("Listening to the right side…");
                var r = await Transcribe(To16k(pcm, WordsSide.Right), cancel);
                bool left = WordCount(l) > WordCount(r);
                return (left ? l : r, $" (from the {(left ? "left" : "right")} side, where the singing is)");
            }
            status("Listening to the song…");
            return (await Transcribe(To16k(pcm, side == WordsSide.Auto ? WordsSide.Both : side), cancel), "");
        }
    }
}
