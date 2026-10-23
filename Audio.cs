using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NAudio.Wave;
using NrgToMp3;

namespace NrgToMp3Win
{
    /// <summary>Windows-only audio: MP3 encoding (song reading is in Disc.cs).</summary>
    static partial class Audio
    {
        public static readonly WaveFormat CdFormat = new WaveFormat(44100, 16, 2);

        /// <summary>MP3 with Windows' built-in encoder (Media Foundation, part of Windows 10 and 11).</summary>
        public static void EncodeMp3(string pcmPath, string mp3Path)
        {
            // Open the file ourselves so it's certainly closed afterwards (the converter deletes it next),
            // and allow deleting while open, as Windows otherwise refuses.
            using (var file = new FileStream(pcmPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new RawSourceWaveStream(file, CdFormat))
                MediaFoundationEncoder.EncodeToMp3(reader, mp3Path, Program.Mp3BitRateKbps * 1000);
        }
    }

    /// <summary>Plays songs for previewing: one at a time, or a whole disc in order ("Play All").</summary>
    class PreviewPlayer : IDisposable
    {
        public record Item(string Id, string Title, Func<byte[]> Load);

        WaveOutEvent output;
        RawSourceWaveStream stream;
        List<Item> queue = new();
        int index;
        Task<byte[]> nextSong;               // the next song, read in the background while this one plays
        public string PlayingId { get; private set; }
        public string Title { get; private set; } = "";
        public event Action Changed;

        public bool IsPlaying(string id) => PlayingId == id;
        public TimeSpan Position => stream?.CurrentTime ?? TimeSpan.Zero;
        public TimeSpan Length => stream?.TotalTime ?? TimeSpan.Zero;
        public int QueueCount => queue.Count;
        public int QueueIndex => index;
        public bool HasNext => queue.Count > 0 && index < queue.Count - 1;
        public bool Paused { get; private set; }
        public bool RepeatAll { get; set; }               // Player tab: start the list again after the last song
        float volume = 1f;
        public float Volume { get => volume; set { volume = Math.Clamp(value, 0f, 1f); if (output != null) output.Volume = volume; } }

        public void Pause() { if (output != null && !Paused) { output.Pause(); Paused = true; Changed?.Invoke(); } }
        public void Resume() { if (output != null && Paused) { output.Play(); Paused = false; Changed?.Invoke(); } }
        public void TogglePause() { if (Paused) Resume(); else Pause(); }

        /// <summary>One song on its own.</summary>
        public void Play(string id, string title, byte[] pcm)
        {
            Stop();
            Start(new Item(id, title, () => pcm), pcm);
        }

        /// <summary>Every song in order from <paramref name="from"/>, moving straight on as each one ends.</summary>
        public void PlayAll(List<Item> items, int from = 0)
        {
            Stop();
            if (from < 0 || from >= items.Count) return;
            queue = items;
            index = from;
            Start(items[from], items[from].Load());
        }

        public void Next() { if (HasNext) { index++; Start(queue[index], TakeNext()); } }

        /// <summary>Like a CD player: back to the start of this song, or (near its start) to the previous song.</summary>
        public void Previous()
        {
            if (Position.TotalSeconds > 3 || queue.Count == 0 || index == 0) { SeekSeconds(0); return; }
            index--;
            Start(queue[index], queue[index].Load());
        }

        /// <summary>The last 10 seconds: with Play All, the quickest way to hear each song run into the next.</summary>
        public void LastTen() => SeekSeconds(Math.Max(0, Length.TotalSeconds - 10));

        public void Seek(double fraction) => SeekSeconds(Length.TotalSeconds * Math.Clamp(fraction, 0, 1));

        public void SeekSeconds(double t)
        {
            if (stream == null) return;
            long pos = Math.Clamp((long)(t * 176400), 0, stream.Length);
            stream.Position = pos - pos % 4;
        }

        byte[] TakeNext()
        {
            var pre = nextSong;
            nextSong = null;
            try { if (pre != null) return pre.Result; } catch { }
            return queue[index].Load();
        }

        void Start(Item item, byte[] pcm)
        {
            Halt();
            stream = new RawSourceWaveStream(new MemoryStream(pcm), Audio.CdFormat);
            output = new WaveOutEvent { DesiredLatency = 200 };
            output.Init(stream);
            output.Volume = volume;
            Paused = false;
            var id = item.Id;
            output.PlaybackStopped += (_, _) =>
            {
                if (PlayingId != id) return;          // stopped by us, not by reaching the end
                if (HasNext) Next();
                else if (RepeatAll && queue.Count > 0) { index = 0; Start(queue[0], queue[0].Load()); }
                else Stop();
            };
            output.Play();
            PlayingId = id;
            Title = item.Title;
            if (HasNext) { var load = queue[index + 1].Load; nextSong = Task.Run(load); }
            Changed?.Invoke();
        }

        /// <summary>Stops the sound but keeps the Play All list.</summary>
        void Halt()
        {
            var o = output;
            output = null;
            PlayingId = null;
            Paused = false;
            if (o != null) { try { o.Stop(); } catch { } o.Dispose(); }
            stream?.Dispose();
            stream = null;
        }

        public void Stop()
        {
            bool was = PlayingId != null;
            Halt();
            queue = new(); index = 0; nextSong = null;
            if (was) Changed?.Invoke();
        }

        public void Dispose() => Stop();
    }
}
