using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using NAudio.Wave;

namespace NrgToMp3Win
{
    /// <summary>A music player for songs from audio files and disc images, with playlists that can be saved and opened
    /// (the same ".nrgplaylist" files as the Mac app).</summary>
    class PlayerTab : UserControl
    {
        readonly List<PlayerSong> songs = new();
        readonly PreviewPlayer player = new();
        readonly Label nowTitle = new() { AutoSize = true, Font = new Font("Segoe UI", 14f, FontStyle.Bold), Text = "Nothing playing" };
        readonly Label nowInfo = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Text = "Choose a song below, or press Play to start the playlist" };
        readonly TrackBar seek = new() { Dock = DockStyle.Fill, Maximum = 1000, TickStyle = TickStyle.None };
        readonly Label time = new() { AutoSize = true, Font = new Font("Segoe UI", 10f), Margin = new Padding(3, 8, 3, 3) };
        readonly Button play = new() { Text = "▶  Play", Width = 110, Height = 40, Font = new Font("Segoe UI", 11f, FontStyle.Bold) };
        readonly CheckBox repeat = new() { Text = "Repeat", Appearance = Appearance.Button, AutoSize = true };
        readonly TrackBar volume = new() { Width = 140, Minimum = 0, Maximum = 100, TickStyle = TickStyle.None };
        readonly TextBox name = new() { Width = 260, PlaceholderText = "Playlist name (optional)" };
        readonly DataGridView grid = new()
        {
            Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
            RowHeadersVisible = false, BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.None,
            EditMode = DataGridViewEditMode.EditOnEnter, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
        };
        readonly Label summary = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 8, 3, 3) };
        readonly Label status = new() { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        readonly Timer timer = new() { Interval = 250 };
        bool building;

        public PlayerTab()
        {
            AllowDrop = true;
            DragEnter += (_, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            DragDrop += (_, e) => Add((string[])e.Data.GetData(DataFormats.FileDrop));

            // ---- now playing
            var prev = new Button { Text = "⏮", Width = 48, Height = 40, Font = new Font("Segoe UI", 12f) };
            var stop = new Button { Text = "⏹", Width = 48, Height = 40, Font = new Font("Segoe UI", 12f) };
            var next = new Button { Text = "⏭", Width = 48, Height = 40, Font = new Font("Segoe UI", 12f) };
            var last10 = new Button { Text = "Last 10 s", AutoSize = true, Margin = new Padding(3, 10, 3, 3) };
            prev.Click += (_, _) => player.Previous();
            play.Click += (_, _) => PlayPause();
            stop.Click += (_, _) => player.Stop();
            next.Click += (_, _) => player.Next();
            last10.Click += (_, _) => player.LastTen();
            repeat.CheckedChanged += (_, _) => player.RepeatAll = repeat.Checked;
            volume.Value = (int)Math.Round(float.TryParse(Settings.Get("volume", "1"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v * 100 : 100);
            player.Volume = volume.Value / 100f;
            volume.ValueChanged += (_, _) => { player.Volume = volume.Value / 100f; Settings.Set("volume", (volume.Value / 100f).ToString(System.Globalization.CultureInfo.InvariantCulture)); };
            seek.Scroll += (_, _) => player.Seek(seek.Value / 1000.0);
            var tips = new ToolTip();
            tips.SetToolTip(prev, "Previous song (or back to the start of this one)");
            tips.SetToolTip(next, "Next song");
            tips.SetToolTip(last10, "Jump to the last 10 seconds of the song");
            tips.SetToolTip(repeat, "Start the playlist again after the last song");

            var titles = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown };
            titles.Controls.AddRange(new Control[] { nowTitle, nowInfo });
            var seekRow = new Panel { Dock = DockStyle.Top, Height = 36 };
            seekRow.Controls.Add(seek);
            var timeBox = new Panel { Dock = DockStyle.Right, Width = 120 }; timeBox.Controls.Add(time);
            seekRow.Controls.Add(timeBox);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            buttons.Controls.AddRange(new Control[] { prev, play, stop, next, last10, repeat,
                new Label { Text = "   🔈", AutoSize = true, Margin = new Padding(3, 12, 0, 3) }, volume, new Label { Text = "🔊", AutoSize = true, Margin = new Padding(0, 12, 3, 3) } });
            repeat.Margin = new Padding(3, 10, 3, 3);
            volume.Margin = new Padding(0, 10, 0, 3);
            var nowPanel = new Panel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10), BorderStyle = BorderStyle.FixedSingle };
            nowPanel.Controls.Add(buttons); nowPanel.Controls.Add(seekRow); nowPanel.Controls.Add(titles);

            // ---- playlist
            var open = new Button { Text = "Open Playlist…", AutoSize = true };
            var save = new Button { Text = "Save Playlist…", AutoSize = true };
            open.Click += (_, _) => OpenPlaylist();
            save.Click += (_, _) => SavePlaylist();
            var listHeader = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 10, 0, 0) };
            listHeader.Controls.AddRange(new Control[] { new Label { Text = "Playlist", AutoSize = true, Font = new Font("Segoe UI", 11f, FontStyle.Bold), Margin = new Padding(3, 6, 3, 3) }, name, open, save });

            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "num", HeaderText = "#", Width = 40, ReadOnly = true });
            grid.Columns.Add(new DataGridViewButtonColumn { Name = "play", HeaderText = "", Width = 40 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "title", HeaderText = "Song (click to rename)", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "source", HeaderText = "From", Width = 260, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "len", HeaderText = "Length", Width = 70, ReadOnly = true });
            foreach (var (n, t, tip) in new[] { ("up", "▲", "Move up"), ("down", "▼", "Move down"), ("remove", "✕", "Remove from the playlist") })
                grid.Columns.Add(new DataGridViewButtonColumn { Name = n, HeaderText = "", Width = 36, Text = t, UseColumnTextForButtonValue = true, ToolTipText = tip });
            grid.CellContentClick += GridClick;
            grid.CellEndEdit += (_, e) =>
            {
                if (building || e.RowIndex < 0 || e.RowIndex >= songs.Count || grid.Columns[e.ColumnIndex].Name != "title") return;
                songs[e.RowIndex].Title = grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value as string ?? songs[e.RowIndex].Title;
            };
            grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && grid.Columns[e.ColumnIndex].Name != "title") PlayFrom(e.RowIndex); };

            var addFiles = new Button { Text = "Add Files…", AutoSize = true };
            var addImage = new Button { Text = "Add Disc Image…", AutoSize = true };
            var clear = new Button { Text = "Clear", AutoSize = true };
            addFiles.Click += (_, _) => { using var d = new OpenFileDialog { Multiselect = true, Filter = "Songs|*.wav;*.mp3;*.m4a;*.aif;*.aiff;*.wma;*.flac;*.aac|All files|*.*" }; if (d.ShowDialog(this) == DialogResult.OK) Add(d.FileNames); };
            addImage.Click += (_, _) => { using var d = new OpenFileDialog { Multiselect = true, Filter = "Disc images (*.nrg;*.cue)|*.nrg;*.cue" }; if (d.ShowDialog(this) == DialogResult.OK) Add(d.FileNames); };
            clear.Click += (_, _) => { player.Stop(); songs.Clear(); Rebuild(); };
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            bottom.Controls.AddRange(new Control[] { addFiles, addImage, clear, summary });
            var hint = new Label { Dock = DockStyle.Bottom, AutoSize = true, ForeColor = SystemColors.GrayText, Text = "Drag songs (WAV, MP3, M4A, …), .nrg / .cue disc images or saved playlists onto this tab. Double-click a song to play from there." };

            Controls.Add(grid); Controls.Add(listHeader); Controls.Add(nowPanel); Controls.Add(hint); Controls.Add(bottom); Controls.Add(status);

            player.Changed += () => BeginInvoke(UpdateNowPlaying);
            timer.Tick += (_, _) =>
            {
                if (player.PlayingId == null || player.Length.TotalSeconds <= 0) return;
                if (!seek.Capture) seek.Value = (int)Math.Clamp(player.Position.TotalSeconds / player.Length.TotalSeconds * 1000, 0, 1000);
                time.Text = $"{SongCore.Short(player.Position.TotalSeconds)} / {SongCore.Short(player.Length.TotalSeconds)}";
            };
            timer.Start();
            Rebuild();
            UpdateNowPlaying();
        }

        /// <summary>The playlist as player items, starting the queue from any song.</summary>
        List<PreviewPlayer.Item> Items() => songs.Select(s => new PreviewPlayer.Item(s.Id.ToString(), s.Title, () =>
            s.File != null ? SongFiles.Decode(s.File) : Audio.ReadSong(s.Image, s.NrgTrack, s.Swap))).ToList();

        void PlayFrom(int i)
        {
            if (i < 0 || i >= songs.Count) return;
            try { player.PlayAll(Items(), i); }
            catch (Exception ex) { status.Text = "Couldn't play: " + ex.Message; }
        }

        void PlayPause()
        {
            if (player.PlayingId != null) { player.TogglePause(); return; }
            PlayFrom(grid.CurrentRow?.Index ?? 0);
        }

        void UpdateNowPlaying()
        {
            bool on = player.PlayingId != null;
            nowTitle.Text = on ? player.Title : "Nothing playing";
            nowInfo.Text = on ? (player.QueueCount > 0 ? $"Song {player.QueueIndex + 1} of {player.QueueCount}" : "") + (player.Paused ? " · paused" : "")
                              : "Choose a song below, or press Play to start the playlist";
            play.Text = on && !player.Paused ? "⏸  Pause" : "▶  Play";
            if (!on) { seek.Value = 0; time.Text = ""; }
            building = true;
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.Index >= songs.Count) continue;
                bool me = player.IsPlaying(songs[row.Index].Id.ToString());
                row.Cells["play"].Value = me ? (player.Paused ? "▶" : "🔊") : "▶";
                row.DefaultCellStyle.BackColor = me ? Color.FromArgb(204, 228, 247) : Color.Empty;
                row.DefaultCellStyle.ForeColor = me ? Color.Black : Color.Empty;
                row.DefaultCellStyle.Font = me ? new Font(grid.Font, FontStyle.Bold) : null;
            }
            building = false;
        }

        void Rebuild()
        {
            if (grid.IsCurrentCellInEditMode) grid.EndEdit();
            building = true;
            grid.Rows.Clear();
            for (int i = 0; i < songs.Count; i++)
                grid.Rows.Add(i + 1, "▶", songs[i].Title, songs[i].Source, SongCore.Short(songs[i].Seconds));
            building = false;
            summary.Text = $"   {songs.Count} song(s) · {SongCore.Short(songs.Sum(s => s.Seconds))}";
            UpdateNowPlaying();
        }

        void GridClick(object sender, DataGridViewCellEventArgs e)
        {
            int i = e.RowIndex;
            if (i < 0 || i >= songs.Count) return;
            switch (grid.Columns[e.ColumnIndex].Name)
            {
                case "play":
                    if (player.IsPlaying(songs[i].Id.ToString())) player.TogglePause(); else PlayFrom(i);
                    return;
                case "up" when i > 0: (songs[i], songs[i - 1]) = (songs[i - 1], songs[i]); break;
                case "down" when i < songs.Count - 1: (songs[i], songs[i + 1]) = (songs[i + 1], songs[i]); break;
                case "remove": songs.RemoveAt(i); break;
                default: return;
            }
            BeginInvoke(Rebuild);   // after this click has finished
        }

        static double SecondsOf(string file)
        {
            try { using var r = new MediaFoundationReader(file); return r.TotalTime.TotalSeconds; } catch { return 0; }
        }

        void Add(IEnumerable<string> paths)
        {
            foreach (var p in paths)
            {
                var ext = Path.GetExtension(p).ToLowerInvariant();
                if (ext == ".nrgplaylist") { LoadPlaylist(p); continue; }
                if (SongFiles.ImageExtensions.Contains(ext))
                {
                    status.Text = $"Reading {Path.GetFileName(p)}…";
                    var d = new DiscItem { Path = p };
                    Task.Run(d.Load).ContinueWith(_ => BeginInvoke(() =>
                    {
                        if (d.Error != null) { status.Text = $"Couldn't read {Path.GetFileName(p)}: {d.Error}"; return; }
                        songs.AddRange(Playlists.FromImage(d));
                        status.Text = $"Added {d.Songs.Count} song(s) from {Path.GetFileName(p)}.";
                        Rebuild();
                    }));
                }
                else if (SongFiles.AudioExtensions.Contains(ext))
                    songs.Add(new PlayerSong { Title = Path.GetFileNameWithoutExtension(p), File = p, Seconds = SecondsOf(p) });
                else if (Directory.Exists(p))
                    Add(Directory.GetFiles(p).Where(f => SongFiles.AudioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())).OrderBy(f => f));
            }
            Rebuild();
        }

        void SavePlaylist()
        {
            if (songs.Count == 0) return;
            using var d = new SaveFileDialog { Filter = "Playlists (*.nrgplaylist)|*.nrgplaylist", FileName = (name.Text.Trim() == "" ? "My Playlist" : name.Text.Trim()) + ".nrgplaylist" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            try { Playlists.Save(d.FileName, name.Text.Trim(), 2, songs); status.Text = $"Saved playlist {Path.GetFileName(d.FileName)}."; }
            catch (Exception ex) { status.Text = "Couldn't save the playlist: " + ex.Message; }
        }

        void OpenPlaylist()
        {
            using var d = new OpenFileDialog { Filter = "Playlists (*.nrgplaylist)|*.nrgplaylist|All files|*.*" };
            if (d.ShowDialog(this) == DialogResult.OK) LoadPlaylist(d.FileName);
        }

        void LoadPlaylist(string path)
        {
            bool replace = true;
            if (songs.Count > 0)
            {
                var answer = MessageBox.Show(this, $"The playlist already has {songs.Count} song(s).\n\nYes: replace them with {Path.GetFileName(path)}\nNo: add its songs to the end",
                                             "Open Playlist", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.Cancel) return;
                replace = answer == DialogResult.Yes;
            }
            try
            {
                var (title, _, loaded) = Playlists.Load(path, out var missing, SecondsOf);
                if (replace) { player.Stop(); songs.Clear(); name.Text = title; }
                songs.AddRange(loaded);
                status.Text = $"Opened {Path.GetFileName(path)}."
                    + (missing.Count == 0 ? "" : $" Couldn't find {missing.Count} song(s) (moved, or on a drive that isn't connected): {string.Join(", ", missing.Take(5))}{(missing.Count > 5 ? "…" : "")}");
                Rebuild();
            }
            catch (Exception ex) { status.Text = "Couldn't open the playlist: " + ex.Message; }
        }

        protected override void Dispose(bool disposing) { if (disposing) { timer.Dispose(); player.Dispose(); } base.Dispose(disposing); }
    }
}
