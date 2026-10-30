using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using NrgToMp3;

namespace NrgToMp3Win
{
    /// <summary>Waveform Viewer: look at and listen to the songs of a disc image or CD, tick neighbouring tracks to see and
    /// hear them joined as one waveform (for discs where one song is spread over several tracks), and fix and save the names.
    /// It never writes audio files, so pressing Enter here can't start an export by accident.</summary>
    class WaveTab : UserControl
    {
        DiscItem disc;
        readonly Dictionary<int, float[]> peaks = new();         // small waveform of each track (left and right together)
        readonly HashSet<int> picked = new();
        byte[] joined;                                           // the ticked tracks end to end
        List<(int number, double at)> bounds = new();            // where each ticked track starts, in seconds
        int peakJob, joinJob;
        string loaded;                                           // what's in the player: a track number, "joined", or null
        bool building;

        readonly SongPlayer player = new();
        readonly Label title = new() { AutoSize = true, Font = new Font("Segoe UI", 11f, FontStyle.Bold), Margin = new Padding(3, 6, 3, 3), Text = "〰 Waveform Viewer" };
        readonly Button closeButton = new() { Text = "Close", AutoSize = true, Visible = false };
        readonly Label empty = new()
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 11f), ForeColor = SystemColors.GrayText,
            Text = "Drag a .nrg or .cue disc image here, or click Open CD for the music CD in the drive.\n\n"
                 + "See every track's waveform, tick tracks to hear them joined, and fix the song names.\nNothing is exported from this tab.",
        };
        readonly Panel content = new() { Dock = DockStyle.Fill, Visible = false };
        readonly TextBox album = new() { Width = 260, PlaceholderText = "Album" }, artist = new() { Width = 200, PlaceholderText = "Artist" };
        readonly Label namesNote = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 4, 3, 3) };
        readonly Button toConvert = new() { Text = "Open in Convert Tab", AutoSize = true };
        readonly DataGridView grid = new()
        {
            Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
            RowHeadersVisible = false, BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.None,
            EditMode = DataGridViewEditMode.EditOnEnter, SelectionMode = DataGridViewSelectionMode.CellSelect, MultiSelect = false,
        };
        readonly Label joinedInfo = new() { AutoSize = true, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Margin = new Padding(3, 8, 3, 3), Text = "Tick tracks above to see them joined as one waveform." };
        readonly Button playJoined = new() { Text = "▶ Play Joined", AutoSize = true, Enabled = false };
        readonly Button stop = new() { Text = "■ Stop", AutoSize = true };
        readonly Button untick = new() { Text = "Untick All", AutoSize = true };
        readonly JoinedView view = new() { Dock = DockStyle.Fill };
        readonly TextBox groupName = new() { Width = 280, PlaceholderText = "Song name" };
        readonly Label status = new() { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        readonly Timer timer = new() { Interval = 100 };

        /// <summary>Open this image on the Convert tab (to export songs).</summary>
        public static event Action<string> OpenInConvert;

        public WaveTab()
        {
            AllowDrop = true;
            DragEnter += (_, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            DragDrop += (_, e) => { if (((string[])e.Data.GetData(DataFormats.FileDrop)).FirstOrDefault() is string f) Open(f); };

            // ---- top
            var openImage = new Button { Text = "Open Disc Image…", AutoSize = true };
            openImage.Click += (_, _) => { using var d = new OpenFileDialog { Filter = "Disc images (*.nrg;*.cue)|*.nrg;*.cue" }; if (d.ShowDialog(this) == DialogResult.OK) Open(d.FileName); };
            var openCd = new Button { Text = "Open CD", AutoSize = true };
            openCd.Click += (_, _) => OpenCd();
            closeButton.Click += (_, _) => Close();
            var header = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            header.Controls.AddRange(new Control[] { title, openImage, openCd, closeButton });
            var tips = new ToolTip();
            tips.SetToolTip(openCd, "Show the songs on the music CD in the drive");

            // ---- names
            album.TextChanged += (_, _) => { if (disc != null && !building) disc.Album = album.Text; };
            artist.TextChanged += (_, _) => { if (disc != null && !building) disc.Artist = artist.Text; };
            var lookUp = new Button { Text = "Look Up Names…", AutoSize = true };
            lookUp.Click += (_, _) => { EndEdit(); using var f = new LookupForm(disc); if (f.ShowDialog(this) == DialogResult.OK) ShowNames(); };
            var save = new Button { Text = "Save Names", AutoSize = true };
            save.Click += (_, _) => SaveNames();
            var load = new Button { Text = "Load Names…", AutoSize = true };
            load.Click += (_, _) => LoadNames();
            var pull = new Button { Text = "Get from GitHub", AutoSize = true };
            pull.Click += (_, _) => PullNames(disc, automatic: false);
            toConvert.Click += (_, _) => { player.Load(null); OpenInConvert?.Invoke(disc.Path); };
            tips.SetToolTip(save, "Save the album, artist and song names to the names file next to the image (and to GitHub), the same as on the Convert tab");
            tips.SetToolTip(toConvert, "Open this disc on the Convert tab to export songs (save the names first so they come along)");
            var namesRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true };
            namesRow.Controls.AddRange(new Control[] { Gray("Album"), album, Gray("Artist"), artist, lookUp, save, load, pull, toConvert });
            var noteRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            noteRow.Controls.Add(namesNote);

            // ---- tracks
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "tick", HeaderText = "", Width = 32, ToolTipText = "Tick tracks to see and hear them joined below" });
            grid.Columns.Add(new DataGridViewButtonColumn { Name = "play", HeaderText = "", Width = 40 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "num", HeaderText = "#", Width = 40, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "title", HeaderText = "Song (click to rename)", Width = 300 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "wave", HeaderText = "Waveform (click to tick)", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "len", HeaderText = "Length", Width = 65, ReadOnly = true });
            grid.Columns.Add(new DataGridViewButtonColumn { Name = "run", HeaderText = "", Width = 40, Text = "↕", UseColumnTextForButtonValue = true,
                                                            ToolTipText = "Tick every track from the first ticked one to this one" });
            grid.RowTemplate.Height = 30;
            grid.CellPainting += PaintWave;
            grid.CellContentClick += GridClick;
            grid.CellClick += (_, e) => { if (e.RowIndex >= 0 && grid.Columns[e.ColumnIndex].Name == "wave") SetTick(e.RowIndex, !picked.Contains(Song(e.RowIndex).Number)); };
            grid.CurrentCellDirtyStateChanged += (_, _) => { if (grid.CurrentCell is DataGridViewCheckBoxCell) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            grid.CellValueChanged += (_, e) =>
            {
                if (building || e.RowIndex < 0 || disc == null) return;
                var t = Song(e.RowIndex);
                switch (grid.Columns[e.ColumnIndex].Name)
                {
                    case "tick": SetTick(e.RowIndex, grid.Rows[e.RowIndex].Cells["tick"].Value is true); break;
                    case "title":
                        if (grid.Rows[e.RowIndex].Cells["title"].Value is string s && s.Trim() != "") disc.Names[t.Number] = s.Trim();
                        break;
                }
            };
            grid.DataError += (_, e) => e.ThrowException = false;
            var listPanel = new Panel { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
            listPanel.Controls.Add(grid);

            // ---- joined
            playJoined.Click += (_, _) => PlayJoined();
            stop.Click += (_, _) => { player.Pause(); player.Seek(0); };
            untick.Click += (_, _) => { foreach (DataGridViewRow r in grid.Rows) r.Cells["tick"].Value = false; picked.Clear(); RebuildJoined(); };
            view.SeekRequested += t =>
            {
                if (joined == null) return;
                if (loaded != "joined") { player.Load(joined); loaded = "joined"; }
                player.Play(t);
            };
            var applyName = new Button { Text = "Apply Name", AutoSize = true };
            applyName.Click += (_, _) => NameTicked();
            groupName.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; NameTicked(); } };
            tips.SetToolTip(applyName, "Give the ticked tracks this name, numbered as parts: “Amazing Grace (part 1 of 3)” and so on; then Save Names");
            tips.SetToolTip(view, "Orange lines show where each track starts. Click to play from that point.");
            var joinedHeader = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            joinedHeader.Controls.AddRange(new Control[] { joinedInfo, playJoined, stop, untick });
            var nameRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            nameRow.Controls.AddRange(new Control[] { Gray("Name the ticked tracks:"), groupName, applyName });
            var viewBox = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 4) };
            viewBox.Controls.Add(view);
            var joinedPanel = new Panel { Dock = DockStyle.Bottom, Height = Dpi.Fit(250, 180) };
            joinedPanel.Controls.Add(viewBox); joinedPanel.Controls.Add(joinedHeader); joinedPanel.Controls.Add(nameRow);

            content.Controls.Add(listPanel); content.Controls.Add(noteRow); content.Controls.Add(namesRow); content.Controls.Add(joinedPanel);
            Controls.Add(content); Controls.Add(empty); Controls.Add(header); Controls.Add(status);

            player.Changed += () => { if (IsHandleCreated) BeginInvoke(UpdatePlayButtons); };
            timer.Tick += (_, _) =>
            {
                double pos = loaded == "joined" ? player.Position : -1;
                if (view.Position != pos) { view.Position = pos; view.Invalidate(); }
            };
            timer.Start();
            VisibleChanged += (_, _) => { if (!Visible) player.Pause(); };
        }

        static Label Gray(string t) => new() { Text = t, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 7, 3, 3) };

        Nrg.Track Song(int row) => disc.Songs[row];
        List<Nrg.Track> Chosen => disc?.Songs.Where(t => picked.Contains(t.Number)).ToList() ?? new();
        void EndEdit() { if (grid.IsCurrentCellInEditMode) grid.EndEdit(); }

        // ---------- opening

        void Open(string path)
        {
            if (!SongFiles.ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
            {
                status.Text = $"{Path.GetFileName(path)} isn't a disc image (.nrg or .cue).";
                return;
            }
            var d = new DiscItem { Path = path };
            status.Text = $"Reading {Path.GetFileName(path)}…";
            Task.Run(d.Load).ContinueWith(_ => BeginInvoke(() =>
            {
                if (d.Error != null) { status.Text = $"Couldn't read {Path.GetFileName(path)}: {d.Error}"; return; }
                Show(d);
                if (SyncSettings.On) PullNames(d, automatic: true);
            }));
        }

        void OpenCd()
        {
            var letters = CdReader.DrivesWithDiscs();
            if (letters.Count == 0) { status.Text = "No CD found. Put a music CD in the drive, wait a few seconds, then try again."; return; }
            status.Text = $"Reading the CD in {letters[0]}…";
            Task.Run(() => DiscItem.FromCd(letters[0])).ContinueWith(r => BeginInvoke(() =>
            {
                if (r.IsFaulted) { status.Text = "Couldn't read the CD: " + r.Exception?.InnerException?.Message; return; }
                Show(r.Result);
                if (SyncSettings.On) PullNames(r.Result, automatic: true);
            }));
        }

        void Show(DiscItem d)
        {
            player.Load(null); loaded = null;
            disc?.Cd?.Dispose();
            disc = d;
            peaks.Clear(); picked.Clear();
            title.Text = "〰 " + (d.IsCd ? "CD" + (d.Album != "" ? $": {d.Album}" : "") : Path.GetFileNameWithoutExtension(d.Path));
            toConvert.Visible = !d.IsCd;
            status.Text = d.Note ?? (d.IsCd ? "Reading the waveforms from the CD takes a while; you can play and name songs meanwhile." : "");
            content.Visible = closeButton.Visible = true;
            empty.Visible = false;
            ShowNames();
            RebuildJoined();
            ComputePeaks();
        }

        void Close()
        {
            player.Load(null); loaded = null;
            disc?.Cd?.Dispose();
            disc = null; peakJob++; joinJob++;
            content.Visible = closeButton.Visible = false;
            empty.Visible = true;
            title.Text = "〰 Waveform Viewer";
            status.Text = "";
        }

        /// <summary>Fills the names and the track list from the disc (after opening, looking up or loading names).</summary>
        void ShowNames()
        {
            EndEdit();
            building = true;
            album.Text = disc.Album; artist.Text = disc.Artist;
            namesNote.Text = disc.NamesNote ?? "";
            grid.Rows.Clear();
            foreach (var t in disc.Songs)
                grid.Rows.Add(picked.Contains(t.Number), "▶", t.Number.ToString("00"), disc.Name(t), "", SongCore.Short(t.Sectors / 75.0));
            building = false;
            UpdatePlayButtons();
        }

        /// <summary>Small waveforms, one track at a time in the background.</summary>
        void ComputePeaks()
        {
            var d = disc;
            int job = ++peakJob;
            Task.Run(() =>
            {
                foreach (var t in d.Songs)
                {
                    if (job != peakJob) return;
                    byte[] pcm;
                    try { pcm = d.ReadTrack(t); } catch { continue; }
                    var (l, r) = SongCore.Peaks(pcm, 240);
                    var both = l.Zip(r, Math.Max).ToArray();
                    BeginInvoke(() => { if (job == peakJob) { peaks[t.Number] = both; grid.InvalidateColumn(grid.Columns["wave"].Index); } });
                }
                BeginInvoke(() => { if (job == peakJob && d.IsCd && status.Text.StartsWith("Reading the waveforms")) status.Text = ""; });
            });
        }

        void PaintWave(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || disc == null || e.RowIndex >= disc.Songs.Count || grid.Columns[e.ColumnIndex].Name != "wave") return;
            e.PaintBackground(e.CellBounds, false);
            var t = disc.Songs[e.RowIndex];
            var box = Rectangle.Inflate(e.CellBounds, -4, -4);
            bool on = picked.Contains(t.Number);
            using (var bg = new SolidBrush(on ? Color.FromArgb(40, 255, 140, 0) : Color.FromArgb(14, 0, 0, 0))) e.Graphics.FillRectangle(bg, box);
            if (peaks.TryGetValue(t.Number, out var p) && p.Length > 0)
            {
                float bw = box.Width / (float)p.Length, mid = box.Top + box.Height / 2f;
                using var brush = new SolidBrush(on ? Color.DarkOrange : Color.FromArgb(180, 0, 120, 215));
                for (int i = 0; i < p.Length; i++)
                {
                    float h = Math.Max(1, p[i] * (box.Height - 2));
                    e.Graphics.FillRectangle(brush, box.Left + i * bw, mid - h / 2, Math.Max(1, bw), h);
                }
            }
            else TextRenderer.DrawText(e.Graphics, "reading…", grid.Font, box, SystemColors.GrayText, TextFormatFlags.VerticalCenter);
            e.Handled = true;
        }

        // ---------- ticking and joining

        void SetTick(int row, bool on)
        {
            var n = Song(row).Number;
            if (on == picked.Contains(n)) return;
            if (on) picked.Add(n); else picked.Remove(n);
            building = true;
            grid.Rows[row].Cells["tick"].Value = on;
            building = false;
            grid.InvalidateRow(row);
            RebuildJoined();
        }

        void GridClick(object sender, DataGridViewCellEventArgs e)
        {
            int i = e.RowIndex;
            if (i < 0 || disc == null) return;
            switch (grid.Columns[e.ColumnIndex].Name)
            {
                case "play": PlayTrack(Song(i)); break;
                case "run":
                    // tick every track from the first ticked one to this one
                    int first = disc.Songs.FindIndex(t => picked.Contains(t.Number));
                    if (first < 0) { SetTick(i, true); break; }
                    for (int k = Math.Min(first, i); k <= Math.Max(first, i); k++) SetTick(k, true);
                    break;
            }
        }

        /// <summary>The ticked tracks joined end to end, as one waveform with a line where each track starts.</summary>
        void RebuildJoined()
        {
            var tracks = Chosen;
            var d = disc;
            int job = ++joinJob;
            if (loaded == "joined") { player.Load(null); loaded = null; }
            joined = null; playJoined.Enabled = false;
            view.LeftPeaks = view.RightPeaks = Array.Empty<float>(); view.Bounds2 = new(); view.Duration = 1; view.Invalidate();
            if (tracks.Count == 0) { joinedInfo.Text = "Tick tracks above to see them joined as one waveform."; return; }
            joinedInfo.Text = $"{Describe(tracks)} · reading…";
            Task.Run(() =>
            {
                using var ms = new MemoryStream();
                var starts = new List<(int, double)>();
                foreach (var t in tracks)
                {
                    if (job != joinJob) return;
                    starts.Add((t.Number, ms.Length / (double)SongCore.BytesPerSecond));
                    var pcm = d.ReadTrack(t);
                    ms.Write(pcm, 0, pcm.Length);
                }
                var all = ms.ToArray();
                var (l, r) = SongCore.Peaks(all, 1400);
                BeginInvoke(() =>
                {
                    if (job != joinJob) return;
                    joined = all; bounds = starts;
                    view.LeftPeaks = l; view.RightPeaks = r; view.Bounds2 = starts; view.Duration = Math.Max(0.01, SongCore.Seconds(all));
                    view.Invalidate();
                    playJoined.Enabled = true;
                    joinedInfo.Text = $"{Describe(tracks)} · {SongCore.Short(SongCore.Seconds(all))}" + (HasHoles(tracks) ? "   (not next to each other on the disc)" : "");
                });
            }).ContinueWith(t => { if (t.IsFaulted) BeginInvoke(() => { if (job == joinJob) joinedInfo.Text = "Couldn't read the tracks: " + t.Exception?.InnerException?.Message; }); });
        }

        static string Describe(List<Nrg.Track> t) => t.Count == 1 ? $"Track {t[0].Number}" : "Tracks " + string.Join(", ", t.Select(x => x.Number));

        bool HasHoles(List<Nrg.Track> c)
        {
            int a = disc.Songs.IndexOf(c[0]), b = disc.Songs.IndexOf(c[^1]);
            return b - a + 1 != c.Count;
        }

        void NameTicked()
        {
            var name = groupName.Text.Trim();
            var tracks = Chosen;
            if (name == "" || tracks.Count == 0) { status.Text = tracks.Count == 0 ? "Tick the tracks to name first." : "Type a song name first."; return; }
            for (int i = 0; i < tracks.Count; i++)
                disc.Names[tracks[i].Number] = tracks.Count == 1 ? name : $"{name} (part {i + 1} of {tracks.Count})";
            ShowNames();
            status.Text = $"Named {tracks.Count} track(s). Click Save Names to keep them.";
        }

        // ---------- playing

        void PlayTrack(Nrg.Track t)
        {
            string id = t.Number.ToString();
            if (loaded == id) { player.Toggle(); return; }
            player.Load(null); loaded = null;
            var d = disc;
            status.Text = d.IsCd ? "Reading the song from the CD…" : "";
            Task.Run(() => d.ReadTrack(t)).ContinueWith(r => BeginInvoke(() =>
            {
                if (d != disc) return;
                if (r.IsFaulted) { status.Text = "Couldn't play: " + r.Exception?.InnerException?.Message; return; }
                status.Text = "";
                player.Load(r.Result); loaded = id;
                player.Play();
            }));
        }

        void PlayJoined(double from = 0)
        {
            if (joined == null) return;
            if (loaded == "joined" && from == 0) { player.Toggle(); return; }
            if (loaded != "joined") { player.Load(joined); loaded = "joined"; }
            player.Play(from);
        }

        void UpdatePlayButtons()
        {
            if (disc == null) return;
            building = true;
            for (int i = 0; i < grid.Rows.Count && i < disc.Songs.Count; i++)
            {
                bool me = loaded == disc.Songs[i].Number.ToString() && player.Playing;
                grid.Rows[i].Cells["play"].Value = me ? "■" : "▶";
            }
            building = false;
            playJoined.Text = loaded == "joined" && player.Playing ? "⏸ Pause" : "▶ Play Joined";
        }

        // ---------- names (the same files and GitHub folder as the Convert tab)

        void SaveNames()
        {
            if (disc == null) return;
            EndEdit();
            var text = disc.NamesText();
            if (!disc.IsCd)
                try
                {
                    File.WriteAllText(disc.NamesFile, text, new UTF8Encoding(true));
                    disc.NamesNote = $"Names saved to {Path.GetFileName(disc.NamesFile)}; they'll load automatically next time";
                    namesNote.Text = disc.NamesNote;
                    PushNames(disc, text);
                    return;
                }
                catch { }
            // a CD (or a read-only folder): choose where to put the names file
            using var f = new SaveFileDialog { FileName = Path.GetFileName(disc.NamesFile), Filter = "Text files (*.txt)|*.txt",
                Title = disc.IsCd ? "Save the names next to this CD's disc image, if you have one, so they load with it" : "Couldn't save next to the image. Choose where to save the names file." };
            if (f.ShowDialog(this) != DialogResult.OK) return;
            try { File.WriteAllText(f.FileName, text, new UTF8Encoding(true)); disc.NamesNote = $"Names saved to {Path.GetFileName(f.FileName)}"; PushNames(disc, text); }
            catch (Exception ex) { disc.NamesNote = "Couldn't save the names: " + ex.Message; }
            namesNote.Text = disc.NamesNote;
        }

        void LoadNames()
        {
            if (disc == null) return;
            using var f = new OpenFileDialog { Filter = "Text files (*.txt)|*.txt|All files|*.*", Title = "Choose a text file of song names" };
            if (!disc.IsCd) f.InitialDirectory = Path.GetDirectoryName(disc.Path);
            if (f.ShowDialog(this) != DialogResult.OK) return;
            try { disc.NamesNote = $"{disc.LoadNames(f.FileName)} of {disc.Songs.Count} name(s) loaded from {Path.GetFileName(f.FileName)}"; }
            catch (Exception ex) { disc.NamesNote = "Couldn't read the names file: " + ex.Message; }
            ShowNames();
        }

        static string Why(Exception ex) => ex is HttpRequestException or TaskCanceledException ? "couldn't reach GitHub (no internet?)" : ex.Message;

        async void PullNames(DiscItem d, bool automatic)
        {
            if (d == null) return;
            try
            {
                var remote = await NamesSync.Download(SyncSettings.Repo, SyncSettings.Token, d.Path);
                if (remote == null) { if (!automatic) d.NamesNote = $"There's no {NamesSync.RemotePath(d.Path)} on GitHub yet. Save Names will put it there."; }
                else
                {
                    d.NamesNote = $"{d.ApplyNames(remote.Text)} name(s) loaded from GitHub ({NamesSync.RemotePath(d.Path)})";
                    if (!d.IsCd) try { File.WriteAllText(d.NamesFile, remote.Text.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(true)); } catch { }
                }
            }
            catch (Exception ex) { d.NamesNote = (automatic && d.NamesNote != null ? d.NamesNote + " · GitHub: " : "Couldn't get names from GitHub: ") + Why(ex); }
            if (d == disc && !grid.IsCurrentCellInEditMode) ShowNames();
        }

        async void PushNames(DiscItem d, string text)
        {
            if (!SyncSettings.On) return;
            if (string.IsNullOrWhiteSpace(SyncSettings.Token)) { d.NamesNote += ". To upload them to GitHub too, add an access key with GitHub… on the Convert tab"; namesNote.Text = d.NamesNote; return; }
            var saved = d.NamesNote;
            namesNote.Text = saved + " · uploading to GitHub…";
            try
            {
                bool changed = await NamesSync.Upload(SyncSettings.Repo, SyncSettings.Token, d.Path, text);
                d.NamesNote = saved + (changed ? $" and uploaded to GitHub ({NamesSync.RemotePath(d.Path)})" : " · GitHub already has these names");
            }
            catch (Exception ex) { d.NamesNote = saved + " · not uploaded: " + Why(ex); }
            if (d == disc) namesNote.Text = d.NamesNote;
        }

        protected override void Dispose(bool disposing) { if (disposing) { timer.Dispose(); player.Dispose(); disc?.Cd?.Dispose(); } base.Dispose(disposing); }
    }

    /// <summary>The joined tracks' waveform (left on top, right below), an orange line and number where each track starts,
    /// and the play head. Click to play from that point.</summary>
    class JoinedView : Control
    {
        public float[] LeftPeaks = Array.Empty<float>(), RightPeaks = Array.Empty<float>();
        public List<(int number, double at)> Bounds2 = new();
        public double Duration = 1, Position = -1;
        public event Action<double> SeekRequested;

        public JoinedView() { DoubleBuffered = true; ResizeRedraw = true; BackColor = SystemColors.Window; Cursor = Cursors.Hand; }

        float X(double t) => (float)(t / Duration * (Width - 1));

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            using (var border = new Pen(Color.FromArgb(40, 0, 0, 0))) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            float laneH = Height / 2f;
            var colors = new[] { Color.FromArgb(190, 0, 120, 215), Color.FromArgb(190, 0, 150, 136) };
            var lanes = new[] { LeftPeaks, RightPeaks };
            for (int li = 0; li < 2; li++)
            {
                var p = lanes[li];
                if (p.Length == 0) continue;
                float bw = Width / (float)p.Length, mid = laneH * (li + 0.5f);
                using var brush = new SolidBrush(colors[li]);
                for (int i = 0; i < p.Length; i++)
                {
                    float h = Math.Max(1, p[i] * (laneH - 6));
                    g.FillRectangle(brush, i * bw, mid - h / 2, Math.Max(1, bw), h);
                }
            }
            using (var line = new Pen(Color.FromArgb(70, 0, 0, 0))) g.DrawLine(line, 0, laneH, Width, laneH);
            using var font = new Font("Segoe UI", 8f, FontStyle.Bold);
            using var orange = new Pen(Color.DarkOrange, 2);
            for (int i = 0; i < Bounds2.Count; i++)
            {
                float x = X(Bounds2[i].at);
                if (i > 0) g.DrawLine(orange, x, 0, x, Height);
                var label = Bounds2[i].number.ToString();
                var size = g.MeasureString(label, font);
                g.FillRectangle(Brushes.DarkOrange, x + 2, 2, size.Width + 2, size.Height);
                g.DrawString(label, font, Brushes.White, x + 3, 2);
            }
            if (Position >= 0) using (var head = new Pen(Color.Red, 2)) g.DrawLine(head, X(Position), 0, X(Position), Height);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (LeftPeaks.Length > 0) SeekRequested?.Invoke(Math.Clamp(e.X / (double)Math.Max(1, Width - 1), 0, 1) * Duration);
        }
    }
}
