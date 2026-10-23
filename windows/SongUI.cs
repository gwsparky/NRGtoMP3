using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NrgToMp3;
using SoundTouch;
using SoundTouch.Net.NAudioSupport;

namespace NrgToMp3Win
{
    static class SongFiles
    {
        public static readonly string[] AudioExtensions = { ".wav", ".mp3", ".m4a", ".aif", ".aiff", ".wma", ".flac", ".aac" };
        public static readonly string[] ImageExtensions = { ".nrg", ".cue" };
        public const string OpenFilter = "Songs and disc images|*.wav;*.mp3;*.m4a;*.aif;*.aiff;*.wma;*.flac;*.aac;*.nrg;*.cue|All files|*.*";

        /// <summary>Any audio file Windows can play, converted to CD format (44.1 kHz / 16-bit / stereo).</summary>
        public static byte[] Decode(string path)
        {
            using var reader = new MediaFoundationReader(path);
            IWaveProvider source = reader;
            MediaFoundationResampler resampler = null;
            var cd = new WaveFormat(44100, 16, 2);
            if (reader.WaveFormat.SampleRate != 44100 || reader.WaveFormat.BitsPerSample != 16 || reader.WaveFormat.Channels != 2
                || reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm)
                source = resampler = new MediaFoundationResampler(reader, cd) { ResamplerQuality = 60 };   // also turns mono into stereo
            try
            {
                using var ms = new MemoryStream();
                var buf = new byte[176400];
                int n;
                while ((n = source.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                ms.SetLength(ms.Length - ms.Length % 4);
                return ms.ToArray();
            }
            finally { resampler?.Dispose(); }
        }
    }

    /// <summary>Plays a song from any point, optionally pitch-shifted live (for previewing a key change).</summary>
    class SongPlayer : IDisposable
    {
        byte[] pcm = Array.Empty<byte>();
        WaveOutEvent output;
        RawSourceWaveStream raw;
        SoundTouchWaveProvider shifter;
        double pos, semitones;
        double? stopAt;
        readonly bool livePitch;
        public event Action Changed;

        public SongPlayer(bool livePitch = false) => this.livePitch = livePitch;

        public bool Playing => output != null;
        public double Duration => SongCore.Seconds(pcm);
        public double Position => raw != null ? raw.Position / (double)SongCore.BytesPerSecond : pos;

        public double Semitones
        {
            get => semitones;
            set { semitones = value; if (shifter != null) shifter.PitchSemiTones = value; }
        }

        /// <summary>A new song; or, with <paramref name="keepPlace"/>, a changed version of the same song carrying on from the same spot.</summary>
        public void Load(byte[] song, bool keepPlace = false)
        {
            bool was = Playing;
            double at = Position;
            Stop(resetPosition: !keepPlace);
            pcm = song ?? Array.Empty<byte>();
            if (keepPlace) { pos = Math.Min(at, Duration); if (was) Play(); }
            Changed?.Invoke();
        }

        public void Play(double? from = null, double? until = null)
        {
            if (from is double f) pos = Math.Clamp(f, 0, Duration);
            Stop(resetPosition: false);
            if (pos >= Duration - 0.05) pos = 0;
            stopAt = until;
            raw = new RawSourceWaveStream(new MemoryStream(pcm, false), Audio.CdFormat);
            long at = (long)(pos * SongCore.BytesPerSecond);
            raw.Position = at - at % 4;
            IWaveProvider provider = raw;
            if (livePitch)
            {
                shifter = new SoundTouchWaveProvider(raw.ToSampleProvider().ToWaveProvider(), new SoundTouchProcessor()) { PitchSemiTones = semitones };
                provider = new SampleToWaveProvider16(shifter.ToSampleProvider());
            }
            output = new WaveOutEvent { DesiredLatency = 150 };
            output.Init(provider);
            var me = output;
            output.PlaybackStopped += (_, _) => { if (output == me) { pos = 0; Stop(resetPosition: true); Changed?.Invoke(); } };
            output.Play();
            Changed?.Invoke();
        }

        public void Pause() { if (Playing) { Stop(resetPosition: false); Changed?.Invoke(); } }
        public void Toggle() { if (Playing) Pause(); else Play(); }

        public void Seek(double t)
        {
            t = Math.Clamp(t, 0, Duration);
            if (Playing) Play(t, stopAt); else { pos = t; Changed?.Invoke(); }
        }

        /// <summary>Call regularly: stops at the end of a part being previewed.</summary>
        public void Tick() { if (Playing && stopAt is double e && Position >= e) Pause(); }

        void Stop(bool resetPosition)
        {
            if (raw != null) pos = Position;
            var o = output;
            output = null;
            if (o != null) { try { o.Stop(); } catch { } o.Dispose(); }
            raw?.Dispose(); raw = null; shifter = null; stopAt = null;
            if (resetPosition) pos = 0;
        }

        public void Dispose() => Stop(true);
    }

    /// <summary>Left and right waveforms (or one combined), with split points (red), the play head (blue)
    /// and alternate parts shaded. Click to move the play head; drag a red line to move a split point.</summary>
    class WaveformView : Control
    {
        public float[] LeftPeaks = Array.Empty<float>(), RightPeaks = Array.Empty<float>();
        public bool Stereo = true;
        public double Duration = 1, Position;
        public List<double> Markers;                     // null: no split points (Change Key)
        public event Action<double> SeekRequested;
        public event Action<int, double> MarkerMoved;
        int dragMarker = -1;
        bool dragging;

        public WaveformView()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = SystemColors.Window;
            Cursor = Cursors.Hand;
        }

        float X(double t) => Duration > 0 ? (float)(t / Duration * (Width - 1)) : 0;
        double T(int x) => Math.Clamp(x / (double)Math.Max(1, Width - 1), 0, 1) * Duration;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            using (var border = new Pen(Color.FromArgb(40, 0, 0, 0))) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            if (Markers != null)    // shade every other part and number them
            {
                var cuts = new List<double> { 0 }; cuts.AddRange(Markers); cuts.Add(Duration);
                using var shade = new SolidBrush(Color.FromArgb(18, 0, 0, 0));
                using var font = new Font("Segoe UI", 8f, FontStyle.Bold);
                for (int k = 0; k + 1 < cuts.Count; k++)
                {
                    if (k % 2 == 1) g.FillRectangle(shade, X(cuts[k]), 0, X(cuts[k + 1]) - X(cuts[k]), Height);
                    g.DrawString((k + 1).ToString(), font, Brushes.Gray, X(cuts[k]) + 3, 2);
                }
            }
            var lanes = Stereo ? new[] { LeftPeaks, RightPeaks } : new[] { LeftPeaks.Zip(RightPeaks, Math.Max).ToArray() };
            float laneH = Height / (float)lanes.Length;
            var colors = new[] { Color.FromArgb(190, 0, 120, 215), Color.FromArgb(190, 0, 150, 136) };
            for (int li = 0; li < lanes.Length; li++)
            {
                var peaks = lanes[li];
                if (peaks.Length == 0) continue;
                float bw = Width / (float)peaks.Length, mid = laneH * (li + 0.5f);
                using var brush = new SolidBrush(colors[li]);
                for (int i = 0; i < peaks.Length; i++)
                {
                    float h = Math.Max(1, peaks[i] * (laneH - 6));
                    g.FillRectangle(brush, i * bw, mid - h / 2, Math.Max(1, bw), h);
                }
            }
            if (Stereo)
            {
                using var line = new Pen(Color.FromArgb(70, 0, 0, 0));
                g.DrawLine(line, 0, laneH, Width, laneH);
                using var f = new Font("Segoe UI", 8f, FontStyle.Bold);
                g.DrawString("L", f, Brushes.Gray, Width - 14, 2);
                g.DrawString("R", f, Brushes.Gray, Width - 14, laneH + 2);
            }
            if (Markers != null)
                for (int i = 0; i < Markers.Count; i++)
                    using (var p = new Pen(Color.Red, i == dragMarker ? 3 : 2)) g.DrawLine(p, X(Markers[i]), 0, X(Markers[i]), Height);
            using (var head = new Pen(Color.Blue, 2)) g.DrawLine(head, X(Position), 0, X(Position), Height);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            dragging = true;
            dragMarker = Markers?.FindIndex(m => Math.Abs(X(m) - e.X) < 6) ?? -1;   // on a red line: move it; anywhere else: move the play head
            if (dragMarker < 0) SeekRequested?.Invoke(T(e.X));
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!dragging) { Cursor = Markers?.Any(m => Math.Abs(X(m) - e.X) < 6) == true ? Cursors.SizeWE : Cursors.Hand; return; }
            if (dragMarker >= 0) MarkerMoved?.Invoke(dragMarker, T(e.X)); else SeekRequested?.Invoke(T(e.X));
        }

        protected override void OnMouseUp(MouseEventArgs e) { dragging = false; dragMarker = -1; Invalidate(); }
    }

    /// <summary>Opening a song: a drop zone, an Open button, and for disc images a list to pick the song from.
    /// Raises <see cref="Loaded"/> with the song's name and audio.</summary>
    class SongOpener : UserControl
    {
        public event Action<string, byte[]> Loaded;
        readonly Label message = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 11f), ForeColor = SystemColors.GrayText };
        readonly ListView tracks = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, CheckBoxes = true, Visible = false };
        readonly Label pickTitle = new() { Dock = DockStyle.Top, AutoSize = false, Height = 28, Visible = false, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
        readonly FlowLayoutPanel pickButtons = new() { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Visible = false };
        readonly Button combine = new() { Text = "Combine Selected", AutoSize = true, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
        readonly Label picked = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 8, 3, 3) };
        DiscItem image;
        readonly string idle;

        /// <summary>True when the current song came from a disc image with several songs, so "Song List" can go back to it.</summary>
        public bool HasList => image != null && image.Songs.Count > 1;

        public SongOpener(string what)
        {
            idle = $"Drag a song here to {what}\n(WAV, MP3, M4A, AIFF, WMA, FLAC)\nor a .nrg / .cue disc image, then pick the song.\n\nOr click Open Song…";
            message.Text = idle;
            AllowDrop = true;
            BorderStyle = BorderStyle.FixedSingle;
            DragEnter += (_, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            DragDrop += (_, e) => { if (((string[])e.Data.GetData(DataFormats.FileDrop)).FirstOrDefault() is string f) Open(f); };
            tracks.Columns.Add("#", 60); tracks.Columns.Add("Song", 420); tracks.Columns.Add("Length", 80);
            tracks.ItemActivate += (_, _) => Pick();
            tracks.ItemChecked += (_, _) => UpdatePicked();

            var choose = new Button { Text = "Use This Song", AutoSize = true };
            choose.Click += (_, _) => Pick();
            var all = new Button { Text = "Select All", AutoSize = true };
            all.Click += (_, _) => { foreach (ListViewItem i in tracks.Items) i.Checked = true; };
            var none = new Button { Text = "Select None", AutoSize = true };
            none.Click += (_, _) => { foreach (ListViewItem i in tracks.Items) i.Checked = false; };
            combine.Click += (_, _) => Combine();
            var tips = new ToolTip();
            tips.SetToolTip(choose, "Open the highlighted song (or double-click a song)");
            tips.SetToolTip(combine, "Join the ticked songs into one song (neighbouring songs keep their original gap)");
            pickButtons.Controls.AddRange(new Control[] { choose, all, none, combine, picked });
            Controls.Add(tracks); Controls.Add(message); Controls.Add(pickTitle); Controls.Add(pickButtons);
        }

        public void ShowIdle() { image = null; ShowMessage(idle); }

        void ShowMessage(string text) { tracks.Visible = pickTitle.Visible = pickButtons.Visible = false; message.Visible = true; message.Text = text; }

        /// <summary>Back to the disc's song list (ticks and highlight kept).</summary>
        public void ShowList()
        {
            if (!HasList) { ShowIdle(); return; }
            message.Visible = false; tracks.Visible = pickTitle.Visible = pickButtons.Visible = true;
            UpdatePicked();
        }

        void UpdatePicked()
        {
            var on = tracks.CheckedItems.Cast<ListViewItem>().Select(i => (Nrg.Track)i.Tag).ToList();
            combine.Enabled = on.Count >= 2;
            picked.Text = on.Count == 0 ? "Tick songs to combine them into one" : $"{on.Count} ticked · about {SongCore.Short(on.Sum(t => t.Sectors) / 75.0)}";
        }

        public void Browse()
        {
            using var d = new OpenFileDialog { Filter = SongFiles.OpenFilter, Title = "Open a song" };
            if (d.ShowDialog(FindForm()) == DialogResult.OK) Open(d.FileName);
        }

        public void Open(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            image = null;
            ShowMessage($"Reading {Path.GetFileName(path)}…");
            if (SongFiles.ImageExtensions.Contains(ext))
            {
                var d = new DiscItem { Path = path };
                Task.Run(d.Load).ContinueWith(_ => BeginInvoke(() =>
                {
                    if (d.Error != null) { ShowMessage($"Couldn't read {Path.GetFileName(path)}: {d.Error}"); return; }
                    image = d;
                    if (d.Songs.Count == 1) { LoadTrack(d, d.Songs[0]); return; }
                    tracks.Items.Clear();
                    foreach (var t in d.Songs)
                        tracks.Items.Add(new ListViewItem(new[] { t.Number.ToString("00"), d.Name(t), SongCore.Short(t.Sectors / 75.0) }) { Tag = t });
                    pickTitle.Text = $"Songs on {Path.GetFileName(path)}: double-click one, or tick several and Combine Selected";
                    ShowList();
                }));
            }
            else if (SongFiles.AudioExtensions.Contains(ext))
            {
                Task.Run(() => SongFiles.Decode(path)).ContinueWith(t => BeginInvoke(() =>
                {
                    if (t.IsFaulted) { ShowMessage($"Couldn't read {Path.GetFileName(path)}: {t.Exception?.InnerException?.Message}"); return; }
                    Loaded?.Invoke(Path.GetFileNameWithoutExtension(path), t.Result);
                }));
            }
            else ShowMessage($"{Path.GetFileName(path)} isn't a song or disc image this app can open.");
        }

        void Pick()
        {
            var item = tracks.SelectedItems.Count == 1 ? tracks.SelectedItems[0] : tracks.FocusedItem;
            if (image != null && item != null) LoadTrack(image, (Nrg.Track)item.Tag);
        }

        void LoadTrack(DiscItem d, Nrg.Track t)
        {
            ShowMessage("Reading the song…");
            Task.Run(() => Audio.ReadSong(d.Nrg.DataPath, t, d.Swap)).ContinueWith(r => BeginInvoke(() =>
            {
                if (r.IsFaulted) { ShowMessage("Couldn't read the song: " + r.Exception?.InnerException?.Message); return; }
                Loaded?.Invoke(d.Name(t), r.Result);
            }));
        }

        /// <summary>Joins the ticked songs into one. Songs next to each other on the disc keep the original gap
        /// between them; with songs skipped in between, they're joined end to end.</summary>
        void Combine()
        {
            var d = image;
            var chosen = tracks.CheckedItems.Cast<ListViewItem>().Select(i => (Nrg.Track)i.Tag).OrderBy(t => d.Songs.IndexOf(t)).ToList();
            if (d == null || chosen.Count < 2) return;
            string title = chosen.Count == d.Songs.Count ? d.Album
                : chosen.Count == 2 ? $"{d.Name(chosen[0])} + {d.Name(chosen[1])}"
                : $"{d.Name(chosen[0])} + {chosen.Count - 1} more";
            ShowMessage($"Combining {chosen.Count} songs…");
            Task.Run(() => Audio.CombineSongs(d, chosen)).ContinueWith(r => BeginInvoke(() =>
            {
                if (r.IsFaulted) { ShowMessage("Couldn't combine the songs: " + r.Exception?.InnerException?.Message); return; }
                Loaded?.Invoke(title, r.Result);
            }));
        }
    }

    /// <summary>"Save to" folder, MP3/WAV choice, an export button, elapsed time and the converter's log.</summary>
    class ExportBar : UserControl
    {
        public readonly Button ExportButton = new() { AutoSize = true, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
        public readonly ProgressBar Progress = new() { Width = 200, Visible = false, Margin = new Padding(3, 8, 3, 3) };
        readonly Label folderLabel = new() { AutoSize = true, MaximumSize = new Size(480, 0), Margin = new Padding(3, 7, 3, 3) };
        readonly RadioButton mp3 = new() { Text = "MP3", AutoSize = true }, wav = new() { Text = "WAV", AutoSize = true };
        public readonly Label Status = new() { AutoSize = true, Margin = new Padding(3, 7, 3, 3) };
        public readonly FlowLayoutPanel Buttons = new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft };
        readonly TextBox log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9f), BackColor = SystemColors.Window };
        readonly string settingsKey;
        readonly Timer timer = new() { Interval = 500 };
        DateTime? started;
        string label;
        public string Folder { get; private set; }
        public bool Wav => wav.Checked;
        public bool Running => started != null;

        public ExportBar(string settingsKey, string buttonLabel)
        {
            this.settingsKey = settingsKey;
            label = buttonLabel;
            Folder = Settings.Get(settingsKey) ?? Settings.Get("outputFolder");
            Height = 150;
            var choose = new Button { Text = "Choose…", AutoSize = true };
            choose.Click += (_, _) =>
            {
                using var d = new FolderBrowserDialog { Description = "Where should the files be saved?", UseDescriptionForTitle = true };
                if (Folder != null) d.SelectedPath = Folder;
                if (d.ShowDialog(FindForm()) == DialogResult.OK) { Folder = d.SelectedPath; Settings.Set(settingsKey, Folder); Refresh2(); }
            };
            mp3.Checked = !Settings.GetBool("wav"); wav.Checked = !mp3.Checked;
            wav.CheckedChanged += (_, _) => { Settings.Set("wav", wav.Checked); Refresh2(); };
            var row1 = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            row1.Controls.AddRange(new Control[] { new Label { Text = "Save to:", AutoSize = true, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Margin = new Padding(3, 7, 3, 3) }, folderLabel, choose, mp3, wav });
            Buttons.Controls.AddRange(new Control[] { ExportButton, Progress, Status });
            Controls.Add(log); Controls.Add(Buttons); Controls.Add(row1);
            timer.Tick += (_, _) => { if (started is DateTime s) Status.Text = $"Working… {SongCore.Short((DateTime.Now - s).TotalSeconds)} elapsed"; };
            Refresh2();
        }

        public void SetLabel(string text) { label = text; Refresh2(); }

        /// <summary>An extra button just left of the export button.</summary>
        public void AddButton(Button b) { Buttons.Controls.Add(b); Buttons.Controls.SetChildIndex(b, 1); }

        public void Refresh2()
        {
            folderLabel.Text = Folder ?? "Choose a folder";
            ExportButton.Text = $"{label} {(Wav ? "WAV" : "MP3")}";
        }

        /// <summary>Runs an export in the background with the converter's output in the log.</summary>
        public void Run(Func<Action<double>, string> work, bool openFolder = true)
        {
            started = DateTime.Now;
            log.Clear();
            Progress.Value = 0; Progress.Visible = true;
            ExportButton.Enabled = false;
            timer.Start();
            void Log(string s) => BeginInvoke(() => log.AppendText(s.Replace("\r\n", "\n").Replace("\n", "\r\n")));
            void SetProgress(double p) => BeginInvoke(() => Progress.Value = (int)Math.Clamp(p * 100, 0, 100));
            Task.Run(() =>
            {
                lock (SongCore.ConsoleLock)
                {
                    var old = Console.Out;
                    Console.SetOut(new LogWriter(Log));
                    try { return work(SetProgress); }
                    catch (Exception ex) { Console.WriteLine("FAILED: " + ex.Message); return null; }
                    finally { Console.SetOut(old); }
                }
            }).ContinueWith(t => BeginInvoke(() =>
            {
                var took = SongCore.Short((DateTime.Now - started.Value).TotalSeconds);
                started = null; timer.Stop(); Progress.Visible = false; ExportButton.Enabled = true;
                bool failed = t.Result == null || log.Text.Contains("FAILED");
                Status.Text = failed ? "Export failed (see details below)." : $"{t.Result} ({took})";
                if (!failed && openFolder && Folder != null) try { System.Diagnostics.Process.Start("explorer.exe", $"\"{Folder}\""); } catch { }
            }));
        }

        protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }
    }
}
