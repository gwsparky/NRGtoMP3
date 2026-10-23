using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using NrgToMp3;

namespace NrgToMp3Win
{
    /// <summary>Copies a music CD to a CUE/BIN disc image: an exact copy (gaps included) that the Convert tab opens
    /// and the Burn CD tab can burn again. Names can be typed or looked up on MusicBrainz first.</summary>
    class CopyTab : UserControl
    {
        readonly Label folderLabel = new() { AutoSize = true, MaximumSize = new Size(600, 0), Margin = new Padding(3, 7, 3, 3) };
        readonly Panel waiting = new() { Dock = DockStyle.Fill };
        readonly Panel discPanel = new() { Dock = DockStyle.Fill, Visible = false };
        readonly Label discInfo = new() { AutoSize = true, Font = new Font("Segoe UI", 11f, FontStyle.Bold), Margin = new Padding(3, 5, 12, 3) };
        readonly TextBox album = new() { Width = 300, PlaceholderText = "Album (also the image's file name)" }, artist = new() { Width = 220, PlaceholderText = "Artist" };
        readonly Button lookup = new() { Text = "Look Up Names…", AutoSize = true };
        readonly DataGridView grid = new()
        {
            Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
            RowHeadersVisible = false, BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.None,
            EditMode = DataGridViewEditMode.EditOnEnter, SelectionMode = DataGridViewSelectionMode.CellSelect,
        };
        readonly ProgressBar progress = new() { Dock = DockStyle.Top, Maximum = 1000, Visible = false, Height = 18 };
        readonly Label clock = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 7, 3, 3) };
        readonly Label status = new() { AutoSize = true, MaximumSize = new Size(700, 0), Margin = new Padding(3, 7, 3, 3) };
        readonly Button save = new() { Text = "Save Image", AutoSize = true, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
        readonly Button stop = new() { Text = "Stop", AutoSize = true, Visible = false };
        readonly Button ejectButton = new() { Text = "Eject", AutoSize = true };
        readonly Button showFile = new() { Text = "Show in Explorer", AutoSize = true, Visible = false };
        readonly Button openConvert = new() { Text = "Open in Convert Tab", AutoSize = true, Visible = false };
        readonly Button addBurn = new() { Text = "Add to Burn List", AutoSize = true, Visible = false };
        readonly PreviewPlayer player = new();
        readonly Timer poll = new() { Interval = 3000 }, tick = new() { Interval = 500 };
        string folder = Settings.Get("archiveFolder");
        string drive;                     // "D:" of the disc shown
        CdReader.Song[] songs = Array.Empty<CdReader.Song>();
        DiscItem disc;                    // names, album, artist (shared with the Look Up Names window)
        bool running, cancel, building, scanning;
        DateTime started;
        long bytesDone, bytesTotal;
        string savedCue;

        /// <summary>Raised by "Open in Convert Tab".</summary>
        public static event Action<string> OpenInConvert;

        public CopyTab()
        {
            var choose = new Button { Text = "Choose…", AutoSize = true };
            choose.Click += (_, _) =>
            {
                using var d = new FolderBrowserDialog { Description = "Where should copied CDs be saved?", UseDescriptionForTitle = true };
                if (folder != null) d.SelectedPath = folder;
                if (d.ShowDialog(this) == DialogResult.OK) { folder = d.SelectedPath; Settings.Set("archiveFolder", folder); UpdateState(); }
            };
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            top.Controls.AddRange(new Control[] { new Label { Text = "Save images to:", AutoSize = true, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Margin = new Padding(3, 7, 3, 3) }, folderLabel, choose });

            waiting.Controls.Add(new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 12f), ForeColor = SystemColors.GrayText,
                Text = "💿\n\nInsert a music CD to copy it\n\nThe whole disc is saved as a CUE/BIN image, an exact copy you can burn again later.",
            });

            // ---- the disc
            album.TextChanged += (_, _) => { if (disc != null && !building) disc.Album = album.Text; };
            artist.TextChanged += (_, _) => { if (disc != null && !building) disc.Artist = artist.Text; };
            lookup.Click += (_, _) =>
            {
                if (disc == null) return;
                if (grid.IsCurrentCellInEditMode) grid.EndEdit();
                using var f = new LookupForm(disc);
                if (f.ShowDialog(this) == DialogResult.OK) ShowDisc();
            };
            var discHeader = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true };
            discHeader.Controls.Add(discInfo); discHeader.Controls.Add(lookup);
            discHeader.SetFlowBreak(lookup, true);
            discHeader.Controls.AddRange(new Control[] { Gray("Album"), album, Gray("Artist"), artist });
            grid.Columns.Add(new DataGridViewButtonColumn { Name = "play", HeaderText = "", Width = 40 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "num", HeaderText = "#", Width = 40, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "title", HeaderText = "Song name (click to edit)", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "len", HeaderText = "Length", Width = 70, ReadOnly = true });
            grid.CellContentClick += GridClick;
            grid.CellEndEdit += (_, e) =>
            {
                if (building || disc == null || e.RowIndex < 0 || grid.Columns[e.ColumnIndex].Name != "title") return;
                disc.Names[songs[e.RowIndex].Number] = (grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value as string ?? "").Trim();
            };
            discPanel.Controls.Add(grid); discPanel.Controls.Add(discHeader);

            // ---- bottom
            save.Click += (_, _) => Save();
            stop.Click += (_, _) => cancel = true;
            ejectButton.Click += (_, _) => { player.Stop(); if (drive != null) { var d = drive; Task.Run(() => CdReader.Eject(d)); } };
            showFile.Click += (_, _) => { if (savedCue != null) try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{savedCue}\""); } catch { } };
            openConvert.Click += (_, _) => { if (savedCue != null) OpenInConvert?.Invoke(savedCue); };
            addBurn.Click += (_, _) => AddToBurnList();
            new ToolTip().SetToolTip(save, "Copy the whole disc to a CUE/BIN image");
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft };
            buttons.Controls.AddRange(new Control[] { save, stop, ejectButton, addBurn, openConvert, showFile, status });
            var clockRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            clockRow.Controls.Add(clock);
            var bottom = new Panel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(0, 6, 0, 0) };
            bottom.Controls.Add(buttons); bottom.Controls.Add(clockRow); bottom.Controls.Add(progress);

            Controls.Add(discPanel); Controls.Add(waiting); Controls.Add(top); Controls.Add(bottom);
            player.Changed += () => { if (IsHandleCreated) BeginInvoke(UpdatePlayButtons); };
            poll.Tick += (_, _) => Scan();
            tick.Tick += (_, _) => UpdateClock();
            HandleCreated += (_, _) => { Scan(); poll.Start(); };
            VisibleChanged += (_, _) => { if (Visible && IsHandleCreated) Scan(); };
            UpdateState();
        }

        static Label Gray(string t) => new() { Text = t, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 7, 3, 3) };

        /// <summary>Looks for a music CD in any drive (every few seconds, so inserting one is noticed).</summary>
        void Scan()
        {
            if (running || scanning || !Visible) return;
            scanning = true;
            string current = drive;
            Task.Run(() =>
            {
                foreach (var letter in CdReader.DrivesWithDiscs())
                {
                    if (letter == current) return (letter, (CdReader.Song[])null, (Toc)null, (string)null);   // still there
                    try
                    {
                        using var r = new CdReader(letter);
                        if (r.Songs.Count > 0) return (letter, r.Songs.ToArray(), r.Toc, (string)null);
                    }
                    catch { }
                }
                return ((string)null, (CdReader.Song[])null, (Toc)null, (string)null);
            }).ContinueWith(t => BeginInvoke(() =>
            {
                scanning = false;
                if (t.IsFaulted || running) return;
                var (letter, found, toc, _) = t.Result;
                if (letter != null && found == null) return;      // the same disc
                if (letter == null) { if (drive != null) { player.Stop(); drive = null; disc = null; songs = Array.Empty<CdReader.Song>(); savedCue = null; status.Text = ""; UpdateState(); } return; }
                ShowCd(letter, found, toc);
            }));
        }

        void ShowCd(string letter, CdReader.Song[] found, Toc toc)
        {
            player.Stop();
            drive = letter;
            songs = found;
            savedCue = null;
            status.Text = "";
            disc = new DiscItem
            {
                Path = letter + @"\Audio CD", Loading = false, Toc = toc,
                Songs = found.Select(s => new Nrg.Track { Number = s.Number, SectorSize = 2352, Mode = 7, Start = s.Lba * 2352, End = (s.Lba + s.Sectors) * 2352, Title = $"Track {s.Number:00}" }).ToList(),
            };
            disc.Names = disc.Songs.ToDictionary(t => t.Number, t => t.Title);
            disc.Selected = disc.Songs.Select(t => t.Number).ToHashSet();
            ShowDisc();
            UpdateState();
        }

        void ShowDisc()
        {
            if (disc == null) return;
            building = true;
            discInfo.Text = $"💿 {drive}  ·  {songs.Length} songs  ·  {SongCore.Short(songs.Sum(s => s.Sectors) / 75.0)}";
            album.Text = disc.Album; artist.Text = disc.Artist;
            grid.Rows.Clear();
            foreach (var t in disc.Songs) grid.Rows.Add("▶", t.Number.ToString("00"), disc.Name(t), SongCore.Short(t.Sectors / 75.0));
            building = false;
            UpdatePlayButtons();
        }

        void UpdateState()
        {
            folderLabel.Text = folder ?? "Choose a folder";
            folderLabel.ForeColor = folder == null ? SystemColors.GrayText : SystemColors.ControlText;
            bool have = disc != null;
            discPanel.Visible = have; waiting.Visible = !have;
            save.Visible = !running; stop.Visible = running;
            save.Enabled = have && folder != null;
            ejectButton.Visible = have; ejectButton.Enabled = !running;
            album.Enabled = artist.Enabled = lookup.Enabled = grid.Enabled = !running;
            progress.Visible = running;
            showFile.Visible = openConvert.Visible = addBurn.Visible = savedCue != null && !running;
            if (!running) clock.Text = "";
            if (have && folder == null && !running && status.Text == "") status.Text = "Choose a folder to save to first.";
        }

        void UpdatePlayButtons()
        {
            building = true;
            for (int i = 0; i < grid.Rows.Count && i < songs.Length; i++)
            {
                bool me = player.IsPlaying(Id(songs[i]));
                grid.Rows[i].Cells["play"].Value = me ? "■" : "▶";
                grid.Rows[i].DefaultCellStyle.BackColor = me ? Color.FromArgb(204, 228, 247) : Color.Empty;
                grid.Rows[i].DefaultCellStyle.ForeColor = me ? Color.Black : Color.Empty;
            }
            building = false;
        }

        string Id(CdReader.Song s) => drive + "#" + s.Number;

        void GridClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= songs.Length || grid.Columns[e.ColumnIndex].Name != "play" || running) return;
            var s = songs[e.RowIndex];
            if (player.IsPlaying(Id(s))) { player.Stop(); return; }
            string letter = drive, title = disc.Name(disc.Songs[e.RowIndex]);
            status.Text = $"Reading “{title}” from the disc to play it…";
            Task.Run(() => { using var r = new CdReader(letter); return r.ReadSong(s); }).ContinueWith(t => BeginInvoke(() =>
            {
                if (t.IsFaulted) { status.Text = "Couldn't play: " + t.Exception.InnerException.Message; return; }
                status.Text = "";
                if (drive == letter && !running) player.Play(Id(s), title, t.Result);
            }));
        }

        void UpdateClock()
        {
            if (!running) return;
            double elapsed = (DateTime.Now - started).TotalSeconds;
            double rate = elapsed > 3 ? bytesDone / elapsed : 0;           // bytes per second
            progress.Value = bytesTotal > 0 ? (int)Math.Clamp(bytesDone * 1000 / bytesTotal, 0, 1000) : 0;
            clock.Text = $"Elapsed {SongCore.Short(elapsed)}   ·   "
                + (rate > 0 ? $"About {SongCore.Short((bytesTotal - bytesDone) / rate)} left   ·   {rate / 176400:0.0}x speed" : "Estimating time left…")
                + $"   ·   {(bytesTotal > 0 ? bytesDone * 100 / bytesTotal : 0)}%";
        }

        /// <summary>Writes &lt;album&gt;.bin (raw CD audio) and &lt;album&gt;.cue (song starts and names).</summary>
        void Save()
        {
            if (disc == null || folder == null || running) return;
            if (grid.IsCurrentCellInEditMode) grid.EndEdit();
            player.Stop();
            string safe = Program.Clean(disc.Album.Trim());
            if (safe == "") safe = "Audio CD";
            string name = safe;
            for (int k = 2; File.Exists(Path.Combine(folder, name + ".cue")) || File.Exists(Path.Combine(folder, name + ".bin")); k++) name = $"{safe} ({k})";
            string cue = Path.Combine(folder, name + ".cue"), bin = Path.Combine(folder, name + ".bin"), txt = Path.Combine(folder, name + ".txt");
            var namesText = disc.NamesText(Path.GetFileName(cue));     // the names file, shared with the Mac (and GitHub)
            var list = songs;
            var titles = disc.Songs.Select(t => BurnList.NumberedName(t.Number, disc.Name(t))).ToList();
            string albumName = disc.Album.Trim(), artistName = disc.Artist.Trim(), letter = drive;
            running = true; cancel = false; savedCue = null;
            started = DateTime.Now; bytesDone = 0; bytesTotal = list.Sum(s => s.Sectors) * 2352;
            status.Text = "Copying…";
            tick.Start();
            UpdateState();

            Task.Run(() =>
            {
                static string Q(string s) => "\"" + s.Replace("\"", "'") + "\"";
                static string Msf(long sectors) => $"{sectors / 4500:00}:{sectors / 75 % 60:00}:{sectors % 75:00}";
                var text = new StringBuilder();
                if (artistName != "") text.Append($"PERFORMER {Q(artistName)}\r\n");
                if (albumName != "") text.Append($"TITLE {Q(albumName)}\r\n");
                text.Append($"FILE {Q(Path.GetFileName(bin))} BINARY\r\n");
                using var reader = new CdReader(letter);
                using (var output = File.Create(bin))
                {
                    long pos = 0;
                    var buffer = new byte[CdReader.ChunkSectors * 2352];
                    for (int i = 0; i < list.Length; i++)
                    {
                        var s = list[i];
                        text.Append($"  TRACK {i + 1:00} AUDIO\r\n    TITLE {Q(titles[i])}\r\n");
                        if (artistName != "") text.Append($"    PERFORMER {Q(artistName)}\r\n");
                        text.Append($"    INDEX 01 {Msf(pos)}\r\n");
                        int song = i + 1;
                        BeginInvoke(() => status.Text = $"Copying song {song} of {list.Length}…");
                        for (long done = 0; done < s.Sectors; done += CdReader.ChunkSectors)
                        {
                            if (cancel) throw new OperationCanceledException("Stopped.");
                            int n = (int)Math.Min(CdReader.ChunkSectors, s.Sectors - done);
                            try { reader.Read(s.Lba + done, n, buffer); }
                            catch (IOException ex) { throw new IOException($"Song {song}: {ex.Message}"); }
                            output.Write(buffer, 0, n * 2352);
                            bytesDone += n * 2352;
                        }
                        pos += s.Sectors;
                    }
                }
                File.WriteAllText(cue, text.ToString(), new UTF8Encoding(false));
                try { File.WriteAllText(txt, namesText, new UTF8Encoding(true)); } catch { }
            }).ContinueWith(t => BeginInvoke(() =>
            {
                running = false;
                tick.Stop();
                var took = SongCore.Short((DateTime.Now - started).TotalSeconds);
                if (t.IsFaulted || t.IsCanceled)
                {
                    try { File.Delete(bin); } catch { }
                    try { File.Delete(cue); } catch { }
                    var ex = t.Exception?.InnerException;
                    status.ForeColor = Color.Firebrick;
                    status.Text = ex is OperationCanceledException ? "Stopped. Nothing was saved." : "Copy failed: " + (ex?.Message ?? "stopped");
                }
                else
                {
                    savedCue = cue;
                    status.ForeColor = Color.DarkGreen;
                    status.Text = $"Saved {Path.GetFileName(cue)}, {Path.GetFileName(bin)} and the song names ({Path.GetFileName(txt)}) in {took}.";
                    ShareNames(cue, namesText, status.Text);
                }
                UpdateState();
            }));
        }

        /// <summary>Puts the copied CD's song names on GitHub too (if syncing is on and there's an access key).</summary>
        async void ShareNames(string cue, string namesText, string saved)
        {
            if (!SyncSettings.On || string.IsNullOrWhiteSpace(SyncSettings.Token)) return;
            status.Text = saved + " Uploading the names to GitHub…";
            try
            {
                await NamesSync.Upload(SyncSettings.Repo, SyncSettings.Token, cue, namesText);
                status.Text = saved + $" Names uploaded to GitHub ({NamesSync.RemotePath(cue)}).";
            }
            catch (Exception ex)
            {
                status.Text = saved + " Names not uploaded to GitHub: " + (ex is System.Net.Http.HttpRequestException or TaskCanceledException ? "no internet?" : ex.Message);
            }
        }

        void AddToBurnList()
        {
            if (savedCue == null) return;
            var d = new DiscItem { Path = savedCue };
            Task.Run(d.Load).ContinueWith(_ => BeginInvoke(() =>
            {
                if (d.Error != null) { status.Text = "Couldn't read the saved image: " + d.Error; return; }
                if (BurnList.Shared.DiscTitle == "") BurnList.Shared.DiscTitle = d.Album;
                BurnList.Shared.Add(BurnList.FromImage(d));
                status.ForeColor = SystemColors.ControlText;
                status.Text = $"Added {d.Songs.Count} songs to the Burn CD list.";
            }));
        }

        protected override void Dispose(bool disposing) { if (disposing) { poll.Dispose(); tick.Dispose(); player.Dispose(); } base.Dispose(disposing); }
    }
}
