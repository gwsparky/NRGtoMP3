using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// <summary>Sends Console output (the converter's progress) to the log box.</summary>
    class LogWriter : TextWriter
    {
        readonly Action<string> write;
        public LogWriter(Action<string> write) => this.write = write;
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) => write(value.ToString());
        public override void Write(string value) => write(value);
        public override void WriteLine(string value) => write(value + Environment.NewLine);
    }

    class ConvertTab : UserControl
    {
        readonly List<DiscItem> discs = new();
        readonly ListBox discList = new() { Dock = DockStyle.Fill, IntegralHeight = false };
        readonly Label discTitle = new() { AutoSize = true, Font = new Font("Segoe UI", 11f, FontStyle.Bold) };
        readonly Label discInfo = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
        readonly TextBox album = new() { Width = 260 }, artist = new() { Width = 220 };
        readonly TextBox mergeName = new() { Width = 300 };
        readonly Label mergeLabel = new() { Text = "Merged file name", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 7, 3, 3) };
        readonly Label mergeExt = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 7, 3, 3) };
        readonly Button lookup = new() { Text = "Look Up Names…", AutoSize = true };
        readonly Button saveNames = new() { Text = "Save Names", AutoSize = true }, loadNames = new() { Text = "Load Names…", AutoSize = true };
        readonly Button pullNames = new() { Text = "Get from GitHub", AutoSize = true }, syncSetup = new() { Text = "GitHub…", AutoSize = true };
        readonly Label namesNote = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 7, 3, 3) };
        readonly Button all = new() { Text = "Check All", AutoSize = true }, none = new() { Text = "Uncheck All", AutoSize = true };
        readonly Button noneAnywhere = new() { Text = "Uncheck All Discs", AutoSize = true };
        readonly Button playAll = new() { Text = "▶ Play All", AutoSize = true };
        readonly Button addBurn = new() { Text = "Add to Burn List", AutoSize = true };
        readonly DataGridView grid = new()
        {
            Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
            RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.CellSelect, BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.None, EditMode = DataGridViewEditMode.EditOnEnter,
        };
        readonly Label folderLabel = new() { AutoSize = true, MaximumSize = new Size(520, 0) };
        readonly RadioButton mp3 = new() { Text = "MP3 (256 kbps)", AutoSize = true }, wav = new() { Text = "WAV (CD quality)", AutoSize = true };
        readonly CheckBox numberNames = new() { Text = "Number song names", AutoSize = true };
        readonly CheckBox merge = new() { Text = "Merge into one file", AutoSize = true };
        readonly Button export = new() { Text = "Export", AutoSize = true, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
        readonly Label selectedCount = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
        readonly Label elapsed = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
        readonly TextBox log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9f), BackColor = SystemColors.Window };
        readonly Panel nowPlaying = new() { Dock = DockStyle.Bottom, Height = 44, Visible = false, Padding = new Padding(6) };
        readonly Label playingTitle = new() { AutoSize = false, Width = 260, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Left };
        readonly TrackBar seek = new() { Dock = DockStyle.Fill, Maximum = 1000, TickStyle = TickStyle.None };
        readonly Label playingTime = new() { AutoSize = false, Width = 110, TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Right };
        readonly PreviewPlayer player = new();
        readonly Timer timer = new() { Interval = 250 };
        string outputFolder = Settings.Get("outputFolder");
        bool running;
        DateTime started;
        bool updatingGrid;

        DiscItem Current => discList.SelectedItem as DiscItem;

        public ConvertTab()
        {
            AllowDrop = true;
            DragEnter += (_, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            DragDrop += (_, e) => Add((string[])e.Data.GetData(DataFormats.FileDrop));

            // ---- top: output folder, format, numbering
            var choose = new Button { Text = "Choose…", AutoSize = true };
            choose.Click += (_, _) => ChooseFolder();
            var reset = new Button { Text = "Reset", AutoSize = true };
            reset.Click += (_, _) => { outputFolder = null; Settings.Set("outputFolder", (string)null); UpdateFolder(); };
            mp3.Checked = !Settings.GetBool("wav"); wav.Checked = !mp3.Checked;
            wav.CheckedChanged += (_, _) => { Settings.Set("wav", wav.Checked); ShowDisc(); };
            numberNames.Checked = Settings.GetBool("numberNames");
            numberNames.CheckedChanged += (_, _) => Settings.Set("numberNames", numberNames.Checked);
            merge.Checked = Settings.GetBool("merge");
            merge.CheckedChanged += (_, _) => { Settings.Set("merge", merge.Checked); ShowDisc(); };
            new ToolTip().SetToolTip(merge, "Join each disc's ticked songs into a single file named after the album, keeping the original gaps between neighbouring songs");
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true };
            top.Controls.AddRange(new Control[] { Bold("Save to:"), folderLabel, choose, reset });
            top.SetFlowBreak(reset, true);
            top.Controls.AddRange(new Control[] { Bold("Format:"), mp3, wav, numberNames, merge });

            // ---- left: list of disc images
            var add = new Button { Text = "Add Files…", AutoSize = true };
            add.Click += (_, _) => ChooseFiles();
            var remove = new Button { Text = "Remove", AutoSize = true };
            remove.Click += (_, _) => { if (Current is { } d && !running) { StopIfPlaying(d); discs.Remove(d); RefreshList(); } };
            var clear = new Button { Text = "Clear", AutoSize = true };
            clear.Click += (_, _) => { if (!running) { player.Stop(); discs.Clear(); RefreshList(); } };
            var leftButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            leftButtons.Controls.AddRange(new Control[] { add, remove, clear });
            var hint = new Label { Text = "Drag .nrg or .cue disc images (or folders) anywhere on this tab.", Dock = DockStyle.Top, Height = 36, ForeColor = SystemColors.GrayText };
            var left = new Panel { Dock = DockStyle.Fill };
            left.Controls.Add(discList); left.Controls.Add(hint); left.Controls.Add(leftButtons);
            discList.SelectedIndexChanged += (_, _) => ShowDisc();

            // ---- right: the selected disc's songs
            album.TextChanged += (_, _) => { if (Current != null && !updatingGrid) Current.Album = album.Text; };
            artist.TextChanged += (_, _) => { if (Current != null && !updatingGrid) Current.Artist = artist.Text; };
            mergeName.TextChanged += (_, _) => { if (Current != null && !updatingGrid) Current.MergeName = mergeName.Text; };
            lookup.Click += (_, _) =>
            {
                if (Current == null || Current.Error != null || Current.Loading) return;
                using var f = new LookupForm(Current);
                if (f.ShowDialog(this) == DialogResult.OK) ShowDisc();
            };
            all.Click += (_, _) => { if (Current != null) { Current.Selected = Current.Songs.Select(t => t.Number).ToHashSet(); ShowDisc(); } };
            none.Click += (_, _) => { if (Current != null) { Current.Selected.Clear(); ShowDisc(); } };
            var discHeader = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true };
            discHeader.Controls.Add(discTitle); discHeader.SetFlowBreak(discTitle, true);
            discHeader.Controls.Add(discInfo); discHeader.SetFlowBreak(discInfo, true);
            discHeader.Controls.AddRange(new Control[] { Gray("Album"), album, Gray("Artist"), artist, lookup, saveNames, loadNames, pullNames, syncSetup, playAll, addBurn, all, none });
            discHeader.SetFlowBreak(none, true);
            discHeader.Controls.Add(namesNote);
            discHeader.SetFlowBreak(namesNote, true);
            saveNames.Click += (_, _) => SaveNames();
            loadNames.Click += (_, _) => LoadNames();
            pullNames.Click += (_, _) => { if (Current != null) PullNames(Current, false); };
            syncSetup.Click += (_, _) =>
            {
                using var f = new NamesSyncForm();
                if (f.ShowDialog(this) == DialogResult.OK) ShowDisc();
            };
            discHeader.Controls.AddRange(new Control[] { mergeLabel, mergeName, mergeExt });
            playAll.Click += (_, _) => PlayAll(0);
            addBurn.Click += (_, _) =>
            {
                if (Current is not { } d || d.Loading || d.Error != null || d.Selected.Count == 0) return;
                if (grid.IsCurrentCellInEditMode) grid.EndEdit();
                if (BurnList.Shared.DiscTitle == "") BurnList.Shared.DiscTitle = d.Album;
                BurnList.Shared.Add(BurnList.FromTicked(d));
                d.NamesNote = $"Added {d.Selected.Count} ticked song(s) to the Burn CD list";
                ShowDisc();
            };
            new ToolTip().SetToolTip(addBurn, "Add this disc's ticked songs to the list on the Burn CD tab");
            new ToolTip().SetToolTip(playAll, "Play every song in order, to check the tracks");

            grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "", Width = 30, Name = "use" });
            grid.Columns.Add(new DataGridViewButtonColumn { HeaderText = "", Width = 40, Name = "play", FlatStyle = FlatStyle.Flat });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "#", Width = 40, ReadOnly = true, Name = "num" });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Song name (click to edit)", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, Name = "title" });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Length", Width = 70, ReadOnly = true, Name = "len" });
            grid.CellContentClick += GridClick;
            grid.CellValueChanged += GridEdited;
            grid.CurrentCellDirtyStateChanged += (_, _) => { if (grid.CurrentCell is DataGridViewCheckBoxCell) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };

            nowPlaying.Controls.Add(seek); nowPlaying.Controls.Add(playingTime); nowPlaying.Controls.Add(playingTitle);
            var stop = new Button { Text = "■ Stop", AutoSize = true, Dock = DockStyle.Left };
            stop.Click += (_, _) => player.Stop();
            nowPlaying.Controls.Add(stop);
            var prev = new Button { Text = "⏮", Width = 40, Dock = DockStyle.Left };
            var next = new Button { Text = "⏭", Width = 40, Dock = DockStyle.Left };
            var last10 = new Button { Text = "Last 10 s", AutoSize = true, Dock = DockStyle.Right };
            prev.Click += (_, _) => player.Previous();
            next.Click += (_, _) => player.Next();
            last10.Click += (_, _) => player.LastTen();
            var tips = new ToolTip();
            tips.SetToolTip(prev, "Previous song (or back to the start of this one)");
            tips.SetToolTip(next, "Next song");
            tips.SetToolTip(last10, "Jump to the last 10 seconds, to check where this song ends (and, with Play All, how the next one starts)");
            nowPlaying.Controls.Add(last10);
            nowPlaying.Controls.Add(next); nowPlaying.Controls.Add(prev);
            nowPlaying.Controls.SetChildIndex(seek, 0);   // the slider fills whatever room is left
            player.Changed += () => BeginInvoke(() => { prev.Visible = next.Visible = player.QueueCount > 0; next.Enabled = player.HasNext; });
            seek.Scroll += (_, _) => player.Seek(seek.Value / 1000.0);
            player.Changed += () => BeginInvoke(() =>
            {
                nowPlaying.Visible = player.PlayingId != null;
                playingTitle.Text = "  " + (player.QueueCount > 0 ? $"{player.QueueIndex + 1} of {player.QueueCount}: " : "") + player.Title;
                UpdatePlayButtons();
            });
            timer.Tick += (_, _) => Tick();
            timer.Start();

            var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 0, 0) };
            right.Controls.Add(grid); right.Controls.Add(nowPlaying); right.Controls.Add(discHeader);

            var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
            split.Panel1.Controls.Add(left);
            split.Panel2.Controls.Add(right);
            HandleCreated += (_, _) => split.SplitterDistance = 300;

            // ---- bottom: export and log
            export.Click += (_, _) =>
            {
                if (merge.Checked)   // merging: confirm the file name(s) first
                {
                    var jobs = discs.Where(d => !d.Loading && d.Error == null && d.Selected.Count > 0).ToList();
                    using var f = new MergeNamesForm(jobs, wav.Checked ? ".wav" : ".mp3", outputFolder);
                    if (f.ShowDialog(this) != DialogResult.OK) return;
                    ShowDisc();
                }
                Export();
            };
            var exportBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft };
            exportBar.Controls.AddRange(new Control[] { export, selectedCount, noneAnywhere, elapsed });
            noneAnywhere.Click += (_, _) => { foreach (var d in discs) d.Selected.Clear(); ShowDisc(); };
            tips.SetToolTip(all, "Tick every song on this disc");
            tips.SetToolTip(none, "Untick every song on this disc, then tick just the ones you want");
            tips.SetToolTip(noneAnywhere, "Untick every song on every disc in the list");
            tips.SetToolTip(saveNames, "Save the album, artist and song names to a .txt file next to the image; they load automatically next time");
            tips.SetToolTip(pullNames, "Load the names for this disc from the \"names\" folder on GitHub (e.g. after editing them on github.com)");
            tips.SetToolTip(syncSetup, "Turn GitHub name syncing on or off, and add the access key that lets Save Names upload");
            tips.SetToolTip(loadNames, "Read song names from a text file: one song per line, e.g. \"1. Amazing Grace\"");
            tips.SetToolTip(mergeName, "Name of the single file this disc's ticked songs are merged into. Leave empty to use the first ticked song's name. You can change it again when you export.");
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 170 };
            bottom.Controls.Add(log); bottom.Controls.Add(exportBar);

            Controls.Add(split);
            Controls.Add(top);
            Controls.Add(bottom);
            UpdateFolder();
            ShowDisc();
        }

        static Label Bold(string t) => new() { Text = t, AutoSize = true, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Margin = new Padding(3, 7, 3, 3) };
        static Label Gray(string t) => new() { Text = t, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 7, 3, 3) };
        static string Time(double s) => $"{(int)s / 60}:{(int)s % 60:00}";

        void UpdateFolder() => folderLabel.Text = outputFolder ?? "Same folder as each disc image";

        void ChooseFolder()
        {
            using var d = new FolderBrowserDialog { Description = "Where should the songs be saved?", UseDescriptionForTitle = true };
            if (outputFolder != null) d.SelectedPath = outputFolder;
            if (d.ShowDialog(this) == DialogResult.OK) { outputFolder = d.SelectedPath; Settings.Set("outputFolder", outputFolder); UpdateFolder(); }
        }

        void ChooseFiles()
        {
            using var d = new OpenFileDialog { Multiselect = true, Filter = "Disc images (*.nrg;*.cue)|*.nrg;*.cue|All files|*.*", Title = "Add disc images" };
            if (d.ShowDialog(this) == DialogResult.OK) Add(d.FileNames);
        }

        public void AddFiles(IEnumerable<string> paths) => Add(paths);

        void Add(IEnumerable<string> paths)
        {
            var found = new List<string>();
            foreach (var p in paths)
            {
                if (Directory.Exists(p))
                    found.AddRange(Directory.GetFiles(p, "*.nrg").Concat(Directory.GetFiles(p, "*.cue")).OrderBy(f => f));
                else if (new[] { ".nrg", ".cue" }.Contains(System.IO.Path.GetExtension(p).ToLowerInvariant()))
                    found.Add(p);
            }
            foreach (var f in found.Where(f => !discs.Any(d => string.Equals(d.Path, f, StringComparison.OrdinalIgnoreCase))))
            {
                var d = new DiscItem { Path = f };
                discs.Add(d);
                Task.Run(d.Load).ContinueWith(_ => BeginInvoke(() => { RefreshList(); if (SyncSettings.On) PullNames(d, true); }));
            }
            RefreshList();
            if (Current == null && discs.Count > 0) discList.SelectedIndex = discs.Count - 1;
        }

        void RefreshList()
        {
            var keep = Current;
            discList.BeginUpdate();
            discList.Items.Clear();
            foreach (var d in discs) discList.Items.Add(d);
            discList.SelectedItem = keep != null && discs.Contains(keep) ? keep : discs.LastOrDefault();
            discList.EndUpdate();
            ShowDisc();
        }

        void ShowDisc()
        {
            var d = Current;
            updatingGrid = true;
            grid.Rows.Clear();
            bool ok = d != null && !d.Loading && d.Error == null;
            album.Enabled = artist.Enabled = lookup.Enabled = all.Enabled = none.Enabled = mergeName.Enabled = ok && !running;
            saveNames.Enabled = loadNames.Enabled = ok && !running;
            pullNames.Enabled = ok && !running && SyncSettings.On;
            namesNote.Text = ok ? d.NamesNote ?? "" : "";
            namesNote.Visible = namesNote.Text != "";
            mergeLabel.Visible = mergeName.Visible = mergeExt.Visible = merge.Checked && ok;
            mergeExt.Text = wav.Checked ? ".wav" : ".mp3";
            playAll.Enabled = ok;
            addBurn.Enabled = ok && d.Selected.Count > 0;
            grid.Enabled = ok;
            if (d == null)
            {
                discTitle.Text = discs.Count == 0 ? "No disc images yet" : "";
                discInfo.Text = discs.Count == 0 ? "Drag .nrg or .cue files here, or click Add Files…" : "";
                album.Text = artist.Text = "";
            }
            else
            {
                discTitle.Text = System.IO.Path.GetFileName(d.Path);
                discInfo.ForeColor = d.Error != null ? Color.Firebrick : d.Note != null ? Color.DarkOrange : SystemColors.GrayText;
                discInfo.Text = d.Loading ? "Reading…" : d.Error ?? $"{d.Songs.Count} songs · {Time(d.Songs.Sum(t => t.Sectors) / 75.0)}" + (d.Note != null ? " · " + d.Note : "");
                album.Text = d.Album; artist.Text = d.Artist;
                mergeName.Text = d.MergeName;
                mergeName.PlaceholderText = d.FirstSongName;
                if (ok)
                    foreach (var t in d.Songs)
                    {
                        int r = grid.Rows.Add(d.Selected.Contains(t.Number), player.IsPlaying(Id(d, t)) ? "■" : "▶", t.Number.ToString("00"), d.Name(t), Time(t.Sectors / 75.0));
                        grid.Rows[r].Tag = t;
                        if (!d.Selected.Contains(t.Number)) grid.Rows[r].DefaultCellStyle.ForeColor = SystemColors.GrayText;
                    }
            }
            updatingGrid = false;
            UpdatePlayButtons();
            UpdateExportButton();
        }

        static string Id(DiscItem d, Nrg.Track t) => d.Path + "#" + t.Number;

        /// <summary>▶ / ■ on each row, without rebuilding the list (which would interrupt a click in progress).</summary>
        void UpdatePlayButtons()
        {
            if (Current is not { } d) return;
            updatingGrid = true;
            foreach (DataGridViewRow row in grid.Rows)
                if (row.Tag is Nrg.Track t)
                {
                    bool on = player.IsPlaying(Id(d, t));
                    row.Cells["play"].Value = on ? "■" : "▶";
                    // highlight the song that's playing (dark text on light blue, readable on any background colour)
                    row.DefaultCellStyle.BackColor = on ? Color.FromArgb(204, 228, 247) : Color.Empty;
                    if (on) row.DefaultCellStyle.ForeColor = Color.Black;
                    else row.DefaultCellStyle.ForeColor = d.Selected.Contains(t.Number) ? Color.Empty : SystemColors.GrayText;
                }
            updatingGrid = false;
        }

        void GridClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || Current is not { } d || grid.Rows[e.RowIndex].Tag is not Nrg.Track t) return;
            if (grid.Columns[e.ColumnIndex].Name != "play") return;
            if (player.IsPlaying(Id(d, t))) { player.Stop(); return; }
            try { player.Play(Id(d, t), d.Name(t), Audio.ReadSong(d.Nrg.DataPath, t, d.Swap)); }
            catch (Exception ex) { MessageBox.Show(this, "Couldn't play this song: " + ex.Message, "NRG to MP3"); }
        }

        void GridEdited(object sender, DataGridViewCellEventArgs e)
        {
            if (updatingGrid || e.RowIndex < 0 || Current is not { } d || grid.Rows[e.RowIndex].Tag is not Nrg.Track t) return;
            var cell = grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
            switch (grid.Columns[e.ColumnIndex].Name)
            {
                case "use":
                    if ((bool)cell.Value) d.Selected.Add(t.Number); else d.Selected.Remove(t.Number);
                    grid.Rows[e.RowIndex].DefaultCellStyle.ForeColor = (bool)cell.Value ? SystemColors.ControlText : SystemColors.GrayText;
                    UpdateExportButton();
                    break;
                case "title":
                    d.Names[t.Number] = (cell.Value as string) ?? "";
                    break;
            }
        }

        void PlayAll(int from)
        {
            if (Current is not { } d || d.Loading || d.Error != null) return;
            var items = d.Songs.Select(t => new PreviewPlayer.Item(Id(d, t), d.Name(t), () => Audio.ReadSong(d.Nrg.DataPath, t, d.Swap))).ToList();
            try { player.PlayAll(items, from); }
            catch (Exception ex) { MessageBox.Show(this, "Couldn't play: " + ex.Message, "NRG to MP3"); }
        }

        void SaveNames()
        {
            if (Current is not { } d || d.Loading || d.Error != null) return;
            if (grid.IsCurrentCellInEditMode) grid.EndEdit();     // keep a name still being typed
            var text = d.NamesText();
            try
            {
                File.WriteAllText(d.NamesFile, text, new UTF8Encoding(true));
                d.NamesNote = $"Names saved to {System.IO.Path.GetFileName(d.NamesFile)}; they'll load automatically next time";
                PushNames(d, text);
            }
            catch
            {
                // the image's folder may be read-only (e.g. on a CD): save somewhere else instead
                using var f = new SaveFileDialog { FileName = System.IO.Path.GetFileName(d.NamesFile), Filter = "Text files (*.txt)|*.txt",
                                                   Title = "Couldn't save next to the image. Choose where to save the names file." };
                if (f.ShowDialog(this) != DialogResult.OK) return;
                try { File.WriteAllText(f.FileName, text, new UTF8Encoding(true)); d.NamesNote = $"Names saved to {System.IO.Path.GetFileName(f.FileName)}"; PushNames(d, text); }
                catch (Exception ex) { d.NamesNote = "Couldn't save the names: " + ex.Message; }
            }
            ShowDisc();
        }

        /// <summary>Loads this disc's names from GitHub if they differ from the names file on this computer
        /// (which is then updated too). Automatic = just opened: stay quiet unless something changed.</summary>
        async void PullNames(DiscItem d, bool automatic)
        {
            if (d.Loading || d.Error != null) return;
            var remotePath = NamesSync.RemotePath(d.Path);
            try
            {
                var remote = await NamesSync.Download(SyncSettings.Repo, SyncSettings.Token, d.Path);
                string local = null;
                try { if (File.Exists(d.NamesFile)) local = File.ReadAllText(d.NamesFile); } catch { }
                if (remote == null)
                {
                    if (!automatic) d.NamesNote = $"There's no {remotePath} on GitHub yet. Save Names will put it there.";
                }
                else if (local != null && NamesSync.Same(remote.Text, local))
                {
                    if (!automatic) d.NamesNote = "The names on this computer already match GitHub";
                }
                else
                {
                    d.NamesNote = $"{d.ApplyNames(remote.Text)} name(s) loaded from GitHub ({remotePath})";
                    try { File.WriteAllText(d.NamesFile, remote.Text.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(true)); }
                    catch { d.NamesNote += "; couldn't update the names file next to the image"; }
                }
            }
            catch (Exception ex)
            {
                var why = ex is HttpRequestException or TaskCanceledException ? "couldn't reach GitHub (no internet?)" : ex.Message;
                d.NamesNote = automatic ? (d.NamesNote != null ? d.NamesNote + " · " : "") + "GitHub: " + why : "Couldn't get names from GitHub: " + why;
            }
            if (d == Current && !grid.IsCurrentCellInEditMode) ShowDisc();
        }

        /// <summary>After Save Names: puts the same names on GitHub (if syncing is on).</summary>
        async void PushNames(DiscItem d, string text)
        {
            if (!SyncSettings.On) return;
            if (string.IsNullOrWhiteSpace(SyncSettings.Token))
            {
                d.NamesNote += ". To upload them to GitHub too, add an access key with GitHub…";
                return;
            }
            var saved = d.NamesNote;
            d.NamesNote = saved + " · uploading to GitHub…";
            try
            {
                bool changed = await NamesSync.Upload(SyncSettings.Repo, SyncSettings.Token, d.Path, text);
                d.NamesNote = saved + (changed ? $" and uploaded to GitHub ({NamesSync.RemotePath(d.Path)})" : " · GitHub already has these names");
            }
            catch (Exception ex)
            {
                var why = ex is HttpRequestException or TaskCanceledException ? "couldn't reach GitHub (no internet?)" : ex.Message;
                d.NamesNote = saved + " · not uploaded: " + why;
            }
            if (d == Current && !grid.IsCurrentCellInEditMode) ShowDisc();
        }

        void LoadNames()
        {
            if (Current is not { } d || d.Loading || d.Error != null) return;
            using var f = new OpenFileDialog { Filter = "Text files (*.txt)|*.txt|All files|*.*", Title = "Choose a text file of song names",
                                               InitialDirectory = System.IO.Path.GetDirectoryName(d.Path) };
            if (f.ShowDialog(this) != DialogResult.OK) return;
            try { d.NamesNote = $"{d.LoadNames(f.FileName)} of {d.Songs.Count} name(s) loaded from {System.IO.Path.GetFileName(f.FileName)}"; }
            catch (Exception ex) { d.NamesNote = "Couldn't read the file: " + ex.Message; }
            ShowDisc();
        }

        void StopIfPlaying(DiscItem d) { if (player.PlayingId?.StartsWith(d.Path + "#") == true) player.Stop(); }

        void Tick()
        {
            if (player.PlayingId != null && player.Length.TotalSeconds > 0)
            {
                if (!seek.Capture) seek.Value = (int)Math.Clamp(player.Position.TotalSeconds / player.Length.TotalSeconds * 1000, 0, 1000);
                playingTime.Text = $"{Time(player.Position.TotalSeconds)} / {Time(player.Length.TotalSeconds)}";
            }
            if (running) elapsed.Text = "Elapsed " + Time((DateTime.Now - started).TotalSeconds) + "   ";
        }

        void UpdateExportButton()
        {
            int n = discs.Where(d => !d.Loading && d.Error == null).Sum(d => d.Selected.Count);
            selectedCount.Text = $"{n} song(s) selected   ";
            export.Text = (merge.Checked ? "Export as One " : "Export ") + (wav.Checked ? "WAV" : "MP3");
            export.Enabled = n > 0 && !running;
            noneAnywhere.Enabled = n > 0 && !running;
        }

        /// <summary>Exports each disc's checked songs, one disc at a time, on a background thread.</summary>
        void Export()
        {
            var jobs = discs.Where(d => !d.Loading && d.Error == null && d.Selected.Count > 0).ToList();
            if (jobs.Count == 0 || running) return;
            player.Stop();
            running = true; started = DateTime.Now;
            log.Clear();
            ShowDisc();
            bool asWav = wav.Checked, numbered = numberNames.Checked, asOne = merge.Checked;
            string folder = outputFolder;
            void Log(string s) => BeginInvoke(() => log.AppendText(s.Replace("\n", "\r\n").Replace("\r\r\n", "\r\n")));

            Task.Run(() =>
            {
                lock (SongCore.ConsoleLock) {
                var oldOut = Console.Out;
                Console.SetOut(new LogWriter(Log));
                int failed = 0;
                try
                {
                    foreach (var d in jobs)
                    {
                        Program.outputDir = folder;
                        if (folder != null) Directory.CreateDirectory(folder);
                        Program.wav = asWav;
                        Program.merge = asOne;
                        Program.onlyTracks = d.Selected.Count < d.Songs.Count ? d.Selected.ToHashSet() : null;
                        Program.meta = new Program.Meta
                        {
                            album = d.Album, artist = d.Artist, numbered = numbered, mergeName = d.MergeName,
                            titles = d.Songs.ToDictionary(t => t.Number.ToString(), t => numbered ? $"{t.Number:00} - {d.Name(t)}" : d.Name(t)),
                        };
                        try { Program.Unpack(d.Path); }
                        catch (Exception ex) { failed++; Console.WriteLine($"  FAILED: {ex.Message}"); }
                    }
                }
                finally { Console.SetOut(oldOut); }
                return failed;
                }
            }).ContinueWith(t => BeginInvoke(() =>
            {
                running = false;
                var took = Time((DateTime.Now - started).TotalSeconds);
                log.AppendText(Environment.NewLine + (t.Result == 0 ? $"All done in {took}." : $"Finished in {took} with {t.Result} failure(s).") + Environment.NewLine);
                elapsed.Text = "";
                ShowDisc();
                if (t.Result == 0)
                {
                    string open = folder ?? System.IO.Path.GetDirectoryName(jobs[0].Path);
                    try { Process.Start("explorer.exe", $"\"{open}\""); } catch { }
                }
            }));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Dispose(); player.Dispose(); }
            base.Dispose(disposing);
        }
    }

    /// <summary>Shown when "Export as One" is clicked: the name of the file each disc will create, ready to change.</summary>
    class MergeNamesForm : Form
    {
        public MergeNamesForm(List<DiscItem> jobs, string ext, string folder)
        {
            Text = jobs.Count == 1 ? "Name the Merged File" : "Name the Merged Files";
            Font = new Font("Segoe UI", 9.5f);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = MaximizeBox = false;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            var list = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12), WrapContents = false };
            list.Controls.Add(new Label { Text = $"Each disc's ticked songs are joined into one {ext.TrimStart('.').ToUpperInvariant()} file with this name.", AutoSize = true, Margin = new Padding(3, 3, 3, 10) });
            var boxes = new List<(DiscItem d, TextBox box)>();
            var create = new Button { Text = jobs.Count == 1 ? "Create File" : $"Create {jobs.Count} Files", AutoSize = true, DialogResult = DialogResult.OK };
            void Check() => create.Enabled = boxes.All(b => b.box.Text.Trim() != "");
            foreach (var d in jobs)
            {
                // pre-fill: whatever was typed under the disc, otherwise the first ticked song's name
                var box = new TextBox { Width = 380, Text = string.IsNullOrWhiteSpace(d.MergeName) ? d.FirstSongName : d.MergeName };
                box.TextChanged += (_, _) => Check();
                var song = new Button { Text = "Song Name", AutoSize = true };
                song.Click += (_, _) => box.Text = d.FirstSongName;
                var albumButton = new Button { Text = "Album Name", AutoSize = true };
                albumButton.Click += (_, _) => box.Text = d.Album;
                var tips = new ToolTip();
                tips.SetToolTip(song, "Use the first ticked song's name: " + d.FirstSongName);
                tips.SetToolTip(albumButton, "Use the album name: " + d.Album);
                var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 0, 0, 10) };
                row.Controls.AddRange(new Control[] { box, new Label { Text = ext, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 6, 6, 3) }, song, albumButton });
                list.Controls.Add(new Label { Text = $"{System.IO.Path.GetFileName(d.Path)} · {d.Selected.Count} song(s)", AutoSize = true, ForeColor = SystemColors.GrayText });
                list.Controls.Add(row);
                boxes.Add((d, box));
            }
            list.Controls.Add(new Label { Text = "Saved in: " + (folder ?? "the same folder as each disc image"), AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(640, 0) });
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
            buttons.Controls.AddRange(new Control[] { create, cancel });
            list.Controls.Add(buttons);
            Controls.Add(list);
            AcceptButton = create; CancelButton = cancel;
            create.Click += (_, _) => { foreach (var (d, box) in boxes) d.MergeName = box.Text.Trim(); };
            Check();
            Theme.Apply(this);
            Dpi.Scale(this);
        }
    }
}
