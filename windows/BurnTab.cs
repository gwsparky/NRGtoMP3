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
    /// <summary>Make an audio CD: a list of songs with exact gaps between them (0 = seamless), burned straight to a
    /// blank CD-R or saved as a CUE/BIN disc image. Songs come from files, disc images, saved playlists, or the
    /// "Add to Burn List" buttons on the other tabs.</summary>
    class BurnTab : UserControl
    {
        readonly BurnList burn = BurnList.Shared;
        readonly PreviewPlayer player = new();
        readonly ComboBox defaultGap = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
        readonly TextBox discTitle = new() { Width = 300, PlaceholderText = "Disc title (optional)" };
        readonly DataGridView grid = new()
        {
            Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
            RowHeadersVisible = false, BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.None,
            EditMode = DataGridViewEditMode.EditOnEnter, SelectionMode = DataGridViewSelectionMode.CellSelect, MultiSelect = false,
        };
        readonly Label empty = new()
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 11f), ForeColor = SystemColors.GrayText,
            Text = "Drag songs (WAV, MP3, M4A, …), .nrg / .cue disc images or saved playlists here,\nor use “Add to Burn List” on the other tabs.",
        };
        readonly ProgressBar capacity = new() { Dock = DockStyle.Fill, Maximum = 1000 };
        readonly Label capacityText = new() { AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
        readonly ComboBox drives = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
        readonly CheckBox eject = new() { Text = "Eject when done", AutoSize = true, Margin = new Padding(3, 7, 3, 3) };
        readonly Button burnButton = new() { Text = "Burn CD", AutoSize = true, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
        readonly Button saveImage = new() { Text = "Save Disc Image…", AutoSize = true };
        readonly Label status = new() { AutoSize = true, MaximumSize = new Size(900, 0), Margin = new Padding(3, 6, 3, 3) };
        readonly ProgressBar busy = new() { Width = 120, Style = ProgressBarStyle.Marquee, Visible = false, Margin = new Padding(3, 8, 3, 3) };
        readonly Timer timer = new() { Interval = 500 };
        readonly List<Control> lockWhileRunning = new();
        bool building, running;
        DateTime started;
        string runningText = "";

        /// <summary>Raised by ✂: open this song in the Split Song tab; its parts replace it in the list.</summary>
        public static event Action<BurnItem> SplitRequested;

        public BurnTab()
        {
            AllowDrop = true;
            DragEnter += (_, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) && !running ? DragDropEffects.Copy : DragDropEffects.None;
            DragDrop += (_, e) => Add((string[])e.Data.GetData(DataFormats.FileDrop));

            // ---- top: gap, disc title, playlists
            foreach (var g in BurnList.GapChoices) defaultGap.Items.Add(BurnList.GapLabel(g));
            defaultGap.SelectedIndex = Math.Max(0, Array.IndexOf(BurnList.GapChoices, burn.DefaultGap));
            defaultGap.SelectedIndexChanged += (_, _) => { if (!building) burn.SetDefaultGap(BurnList.GapChoices[defaultGap.SelectedIndex]); };
            discTitle.Text = burn.DiscTitle;
            discTitle.TextChanged += (_, _) => { if (!building) burn.DiscTitle = discTitle.Text; };
            var playAll = new Button { Text = "▶ Play All", AutoSize = true };
            playAll.Click += (_, _) => PlayFrom(0);
            var stop = new Button { Text = "■ Stop", AutoSize = true };
            stop.Click += (_, _) => player.Stop();
            var openList = new Button { Text = "Open Playlist…", AutoSize = true };
            openList.Click += (_, _) => OpenPlaylist();
            var saveList = new Button { Text = "Save Playlist…", AutoSize = true };
            saveList.Click += (_, _) => SavePlaylist();
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true };
            var gapHint = Gray("Use “None” for songs split from one recording.");
            var applyAll = new Button { Text = "Apply to All Songs", AutoSize = true };
            applyAll.Click += (_, _) =>
            {
                if (burn.Items.Count == 0) return;
                if (grid.IsCurrentCellInEditMode) grid.EndEdit();
                foreach (var it in burn.Items) it.Gap = null;      // every song follows the gap chosen here
                burn.NotifyChanged();
                status.ForeColor = SystemColors.ControlText;
                status.Text = $"Every song now has a gap of {BurnList.GapLabel(burn.DefaultGap)}.";
            };
            top.Controls.AddRange(new Control[] { Bold("Gap between songs:"), defaultGap, applyAll, gapHint });
            top.SetFlowBreak(gapHint, true);
            top.Controls.AddRange(new Control[] { Bold("Disc title:"), discTitle, playAll, stop, openList, saveList });
            var tips = new ToolTip();
            tips.SetToolTip(applyAll, "Give every song this gap, replacing any gaps set song by song (including a disc image's original gaps)");
            tips.SetToolTip(discTitle, "The disc's name; also the name of a saved disc image");
            tips.SetToolTip(playAll, "Listen to the list in order");
            tips.SetToolTip(saveList, "Save this list (songs, order, names and gaps) to open again later, here or on the Mac");

            // ---- the list
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "num", HeaderText = "#", Width = 40, ReadOnly = true });
            grid.Columns.Add(new DataGridViewButtonColumn { Name = "play", HeaderText = "", Width = 40 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "title", HeaderText = "Song (click to rename)", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "source", HeaderText = "From", Width = 220, ReadOnly = true });
            var gapColumn = new DataGridViewComboBoxColumn { Name = "gap", HeaderText = "Gap before", Width = 170, DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton, FlatStyle = FlatStyle.Flat };
            grid.Columns.Add(gapColumn);
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "len", HeaderText = "Length", Width = 65, ReadOnly = true });
            foreach (var (n, t, tip) in new[] { ("split", "✂", "Split this song into several tracks (opens Split Song)"), ("up", "▲", "Move up"), ("down", "▼", "Move down"), ("remove", "✕", "Remove from the list") })
                grid.Columns.Add(new DataGridViewButtonColumn { Name = n, HeaderText = "", Width = 36, Text = t, UseColumnTextForButtonValue = true, ToolTipText = tip });
            grid.CellContentClick += GridClick;
            grid.CellValueChanged += GridEdited;
            grid.CurrentCellDirtyStateChanged += (_, _) => { if (grid.CurrentCell is DataGridViewComboBoxCell) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            grid.DataError += (_, e) => e.ThrowException = false;
            var listPanel = new Panel { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
            listPanel.Controls.Add(grid); listPanel.Controls.Add(empty);

            // ---- capacity
            var capRow = new Panel { Dock = DockStyle.Bottom, Height = 30, Padding = new Padding(0, 6, 0, 0) };
            var capText = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            capText.Controls.Add(capacityText);
            capRow.Controls.Add(capacity); capRow.Controls.Add(capText);

            // ---- buttons
            var addFiles = new Button { Text = "Add Files…", AutoSize = true };
            addFiles.Click += (_, _) => { using var d = new OpenFileDialog { Multiselect = true, Filter = "Songs|*.wav;*.mp3;*.m4a;*.aif;*.aiff;*.wma;*.flac;*.aac|All files|*.*" }; if (d.ShowDialog(this) == DialogResult.OK) Add(d.FileNames); };
            var addImage = new Button { Text = "Add Disc Image…", AutoSize = true };
            addImage.Click += (_, _) => { using var d = new OpenFileDialog { Multiselect = true, Filter = "Disc images (*.nrg;*.cue)|*.nrg;*.cue" }; if (d.ShowDialog(this) == DialogResult.OK) Add(d.FileNames); };
            var clear = new Button { Text = "Clear", AutoSize = true };
            clear.Click += (_, _) => { player.Stop(); burn.Items.Clear(); burn.NotifyChanged(); };
            tips.SetToolTip(addImage, "Add every song from a .nrg or .cue image with its original gaps, to burn a copy of that CD");
            saveImage.Click += (_, _) => SaveImage();
            tips.SetToolTip(saveImage, "Save a CUE/BIN disc image you can burn later");
            burnButton.Click += (_, _) => Burn();
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            buttons.Controls.AddRange(new Control[] { addFiles, addImage, clear });

            // ---- burner
            var refresh = new Button { Text = "Check Again", AutoSize = true };
            refresh.Click += (_, _) => FindDrives();
            eject.Checked = Settings.Get("ejectAfterBurn", "true") == "true";
            eject.CheckedChanged += (_, _) => Settings.Set("ejectAfterBurn", eject.Checked);
            var burnRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true };
            burnRow.Controls.AddRange(new Control[] { Bold("Burner:"), drives, refresh, eject, saveImage, burnButton });
            var statusRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            statusRow.Controls.AddRange(new Control[] { busy, status });

            Controls.Add(listPanel); Controls.Add(top); Controls.Add(capRow); Controls.Add(buttons); Controls.Add(burnRow); Controls.Add(statusRow);
            lockWhileRunning.AddRange(new Control[] { defaultGap, applyAll, discTitle, openList, saveList, addFiles, addImage, clear, refresh, drives, grid });

            burn.Changed += () => { if (IsHandleCreated) BeginInvoke(Rebuild); };
            player.Changed += () => { if (IsHandleCreated) BeginInvoke(UpdatePlayButtons); };
            timer.Tick += (_, _) => { if (running) status.Text = $"{runningText}   ({SongCore.Short((DateTime.Now - started).TotalSeconds)} elapsed)"; };
            timer.Start();
            HandleCreated += (_, _) => FindDrives();
            Rebuild();
        }

        static Label Bold(string t) => new() { Text = t, AutoSize = true, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Margin = new Padding(3, 7, 3, 3) };
        static Label Gray(string t) => new() { Text = t, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 7, 3, 3) };

        void FindDrives()
        {
            drives.Items.Clear();
            drives.Items.Add("Looking for CD burners…");
            drives.SelectedIndex = 0;
            Task.Run(() => { try { return Burner.Drives(); } catch { return new List<Burner.Drive>(); } }).ContinueWith(t => BeginInvoke(() =>
            {
                drives.Items.Clear();
                foreach (var d in t.Result) drives.Items.Add(d);
                drives.DisplayMember = "Name";
                if (drives.Items.Count == 0) drives.Items.Add("No CD burner found. Use Save Disc Image to burn later.");
                drives.SelectedIndex = 0;
                UpdateButtons();
            }));
        }

        Burner.Drive Drive => drives.SelectedItem as Burner.Drive;

        void Rebuild()
        {
            if (grid.IsCurrentCellInEditMode) grid.EndEdit();
            building = true;
            var gapColumn = (DataGridViewComboBoxColumn)grid.Columns["gap"];
            gapColumn.Items.Clear();
            gapColumn.Items.Add($"Default ({BurnList.GapLabel(burn.DefaultGap)})");
            foreach (var g in BurnList.GapChoices) gapColumn.Items.Add(BurnList.GapLabel(g));
            grid.Rows.Clear();
            for (int i = 0; i < burn.Items.Count; i++)
            {
                var it = burn.Items[i];
                int r = grid.Rows.Add(i + 1, "▶", it.Title, it.Source, null, SongCore.Short(it.Seconds));
                var cell = (DataGridViewComboBoxCell)grid.Rows[r].Cells["gap"];
                if (i == 0)
                {
                    // every CD starts with a 2-second pause; that's part of the CD standard
                    var lead = new DataGridViewTextBoxCell { Value = "2 s lead-in" };
                    grid.Rows[r].Cells["gap"] = lead;
                    lead.ReadOnly = true;
                    lead.Style.ForeColor = SystemColors.GrayText;
                    lead.ToolTipText = "Every CD starts with a 2-second pause before the first song; that's part of the CD standard.";
                }
                else
                {
                    // a gap from a disc image may not be one of the choices (e.g. 1.6 s): offer it too
                    if (it.Gap is double g && !BurnList.GapChoices.Contains(g)) cell.Items.Add(BurnList.GapLabel(g));
                    cell.Value = it.Gap is double v ? BurnList.GapLabel(v) : gapColumn.Items[0];
                }
            }
            if (discTitle.Text != burn.DiscTitle) discTitle.Text = burn.DiscTitle;
            int gi = Array.IndexOf(BurnList.GapChoices, burn.DefaultGap);
            if (gi >= 0 && defaultGap.SelectedIndex != gi) defaultGap.SelectedIndex = gi;
            building = false;
            grid.Visible = burn.Items.Count > 0;
            empty.Visible = !grid.Visible;
            UpdatePlayButtons();
            UpdateButtons();
        }

        void UpdateButtons()
        {
            double total = burn.TotalSeconds;
            capacity.Value = (int)Math.Clamp(total / BurnList.Capacity * 1000, 0, 1000);
            capacityText.ForeColor = total > BurnList.Capacity || burn.Items.Count > 99 ? Color.Firebrick : total > 74 * 60 ? Color.DarkOrange : SystemColors.ControlText;
            capacityText.Text = $"{burn.Items.Count} song(s) · {SongCore.Short(total)} of 80:00 (gaps included)"
                + (total > BurnList.Capacity ? " · too long for one CD" : total > 74 * 60 ? " · needs an 80-minute CD-R" : "")
                + (burn.Items.Count > 99 ? " · a CD holds at most 99 songs" : "");
            foreach (var c in lockWhileRunning) c.Enabled = !running;
            saveImage.Enabled = burn.Items.Count > 0 && !running;
            burnButton.Enabled = burn.Items.Count > 0 && !running && Drive != null;
            busy.Visible = running;
        }

        void UpdatePlayButtons()
        {
            building = true;
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.Index >= burn.Items.Count) continue;
                bool me = player.IsPlaying(burn.Items[row.Index].Id.ToString());
                row.Cells["play"].Value = me ? "■" : "▶";
                row.DefaultCellStyle.BackColor = me ? Color.FromArgb(204, 228, 247) : Color.Empty;
                row.DefaultCellStyle.ForeColor = me ? Color.Black : Color.Empty;
            }
            building = false;
        }

        static byte[] Decode(string file) => SongFiles.Decode(file);

        void PlayFrom(int i)
        {
            if (i < 0 || i >= burn.Items.Count) return;
            var items = burn.Items.Select(s => new PreviewPlayer.Item(s.Id.ToString(), s.Title, () => s.Load(Decode))).ToList();
            try { player.PlayAll(items, i); }
            catch (Exception ex) { status.Text = "Couldn't play: " + ex.Message; }
        }

        void GridClick(object sender, DataGridViewCellEventArgs e)
        {
            int i = e.RowIndex;
            if (i < 0 || i >= burn.Items.Count || running) return;
            var items = burn.Items;
            switch (grid.Columns[e.ColumnIndex].Name)
            {
                case "play":
                    if (player.IsPlaying(items[i].Id.ToString())) player.Stop(); else PlayFrom(i);
                    return;
                case "split":
                    player.Stop();
                    SplitRequested?.Invoke(items[i]);
                    return;
                case "up" when i > 0: (items[i], items[i - 1]) = (items[i - 1], items[i]); break;
                case "down" when i < items.Count - 1: (items[i], items[i + 1]) = (items[i + 1], items[i]); break;
                case "remove": if (player.IsPlaying(items[i].Id.ToString())) player.Stop(); items.RemoveAt(i); break;
                default: return;
            }
            BeginInvoke(burn.NotifyChanged);   // after this click has finished
        }

        void GridEdited(object sender, DataGridViewCellEventArgs e)
        {
            int i = e.RowIndex;
            if (building || i < 0 || i >= burn.Items.Count) return;
            var value = grid.Rows[i].Cells[e.ColumnIndex].Value as string;
            switch (grid.Columns[e.ColumnIndex].Name)
            {
                case "title":
                    if (!string.IsNullOrWhiteSpace(value)) burn.Items[i].Title = value.Trim();
                    break;
                case "gap" when i > 0:
                    var choice = BurnList.GapChoices.Cast<double?>().FirstOrDefault(g => BurnList.GapLabel(g.Value) == value);
                    if (choice == null && burn.Items[i].Gap is double old && BurnList.GapLabel(old) == value) choice = old;
                    burn.Items[i].Gap = choice;
                    UpdateButtons();
                    break;
            }
        }

        static double SecondsOf(string file)
        {
            try { using var r = new MediaFoundationReader(file); return r.TotalTime.TotalSeconds; } catch { return 0; }
        }

        void Add(IEnumerable<string> paths)
        {
            if (running) return;
            var added = new List<BurnItem>();
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
                        if (burn.DiscTitle == "") burn.DiscTitle = d.Album;
                        burn.Add(BurnList.FromImage(d));
                        status.Text = $"Added {d.Songs.Count} songs from {Path.GetFileName(p)} with their original gaps."
                            + (d.Nrg.IsRawAudio ? " (Song breaks in this file were guessed from silences.)" : "");
                    }));
                }
                else if (SongFiles.AudioExtensions.Contains(ext))
                    added.Add(new BurnItem { Title = Path.GetFileNameWithoutExtension(p), File = p, Seconds = SecondsOf(p) });
                else if (Directory.Exists(p))
                    added.AddRange(Directory.GetFiles(p).Where(f => SongFiles.AudioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())).OrderBy(f => f)
                        .Select(f => new BurnItem { Title = Path.GetFileNameWithoutExtension(f), File = f, Seconds = SecondsOf(f) }));
            }
            if (added.Count > 0) burn.Add(added);
        }

        void SavePlaylist()
        {
            if (burn.Items.Count == 0) { status.Text = "The list is empty."; return; }
            using var d = new SaveFileDialog { Filter = "Playlists (*.nrgplaylist)|*.nrgplaylist", FileName = Program_Clean(burn.DiscTitle == "" ? "My Playlist" : burn.DiscTitle) + ".nrgplaylist",
                                               Title = "Save playlist (songs made in this app are saved as WAV files in a folder next to it)" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                int n = burn.SavePlaylist(d.FileName);
                status.Text = $"Saved playlist {Path.GetFileName(d.FileName)}" + (n > 0 ? $" ({n} song(s) saved as WAV in “{Path.GetFileNameWithoutExtension(d.FileName)} files”)" : "") + ".";
            }
            catch (Exception ex) { status.Text = "Couldn't save the playlist: " + ex.Message; }
        }

        static string Program_Clean(string s) { var c = NrgToMp3.Program.Clean(s); return c == "" ? "My Playlist" : c; }

        void OpenPlaylist()
        {
            using var d = new OpenFileDialog { Filter = "Playlists (*.nrgplaylist)|*.nrgplaylist|All files|*.*" };
            if (d.ShowDialog(this) == DialogResult.OK) LoadPlaylist(d.FileName);
        }

        void LoadPlaylist(string path)
        {
            bool replace = true;
            if (burn.Items.Count > 0)
            {
                var answer = MessageBox.Show(this, $"The list already has {burn.Items.Count} song(s).\n\nYes: replace them with {Path.GetFileName(path)}\nNo: add its songs to the end",
                                             "Open Playlist", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.Cancel) return;
                replace = answer == DialogResult.Yes;
            }
            player.Stop();
            try
            {
                var missing = burn.LoadPlaylist(path, replace, SecondsOf);
                status.Text = $"Opened {Path.GetFileName(path)}."
                    + (missing.Count == 0 ? "" : $" Couldn't find {missing.Count} song(s) (moved, or on a drive that isn't connected): {string.Join(", ", missing.Take(5))}{(missing.Count > 5 ? "…" : "")}");
            }
            catch (Exception ex) { status.Text = "Couldn't open the playlist: " + ex.Message; }
        }

        void Start(string text) { running = true; started = DateTime.Now; runningText = text; status.ForeColor = SystemColors.ControlText; UpdateButtons(); }

        void Finish(string text, bool ok)
        {
            running = false;
            status.Text = text;
            status.ForeColor = ok ? Color.DarkGreen : Color.Firebrick;
            UpdateButtons();
        }

        void SaveImage()
        {
            if (grid.IsCurrentCellInEditMode) grid.EndEdit();
            if (burn.Items.Count == 0) return;
            using var d = new SaveFileDialog { Filter = "CUE/BIN disc image (*.cue)|*.cue", FileName = Program_Clean(burn.DiscTitle == "" ? "Custom CD" : burn.DiscTitle) + ".cue" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            player.Stop();
            string cue = d.FileName;
            Start("Saving the disc image…");
            Task.Run(() => burn.WriteImage(cue, Decode, s => BeginInvoke(() => runningText = s))).ContinueWith(t => BeginInvoke(() =>
            {
                if (t.IsFaulted) { Finish("Couldn't save the disc image: " + t.Exception.InnerException.Message, false); return; }
                Finish($"Saved {Path.GetFileName(cue)} and {Path.GetFileNameWithoutExtension(cue)}.bin in {SongCore.Short((DateTime.Now - started).TotalSeconds)}.", true);
                try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{cue}\""); } catch { }
            }));
        }

        void Burn()
        {
            if (grid.IsCurrentCellInEditMode) grid.EndEdit();
            if (Drive is not { } drive) return;
            if (burn.Problem() is string problem) { MessageBox.Show(this, problem, "Burn CD", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (MessageBox.Show(this, $"Burn {burn.Items.Count} song(s) ({SongCore.Short(burn.TotalSeconds)}) to the blank CD-R in {drive.Name}?\n\nThe songs are prepared first, then burned. Don't open the drive until it's finished.",
                                "Burn CD", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            player.Stop();
            bool ejectAfter = eject.Checked;
            string temp = Path.Combine(Path.GetTempPath(), "nrg-burn-" + Guid.NewGuid().ToString("N"));
            Start("Preparing the songs…");
            Task.Run(() =>
            {
                Directory.CreateDirectory(temp);
                try
                {
                    var files = burn.WriteTracks(temp, Decode, s => BeginInvoke(() => runningText = s));
                    Burner.Burn(drive.Id, files, ejectAfter, s => BeginInvoke(() => runningText = s));
                }
                finally { try { Directory.Delete(temp, true); } catch { } }
            }).ContinueWith(t => BeginInvoke(() =>
            {
                var error = t.Exception?.InnerException;
                if (error is Burner.ExactGapsFailed) { OfferStandardBurn(drive, ejectAfter, error.Message); return; }
                if (t.IsFaulted) Finish("Burn failed: " + (error?.Message ?? t.Exception.Message), false);
                else Finish($"Burn complete in {SongCore.Short((DateTime.Now - started).TotalSeconds)}.", true);
            }));
        }

        /// <summary>The burner wouldn't burn with exact gaps (nothing was written): offer Windows' standard way instead.</summary>
        void OfferStandardBurn(Burner.Drive drive, bool ejectAfter, string why)
        {
            if (MessageBox.Show(this, "This burner wouldn't burn the CD with your exact gaps. Nothing was written, so the disc is still blank.\n\n"
                                    + $"Windows said: {why}\n\n"
                                    + "Burn it the standard way instead? Every song then has a 2-second gap before it, whatever gaps you chose.",
                                "Burn CD", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                Finish("Not burned. Tip: Save Disc Image… makes a CUE/BIN you can burn with another program.", false);
                return;
            }
            string temp = Path.Combine(Path.GetTempPath(), "nrg-burn-" + Guid.NewGuid().ToString("N"));
            runningText = "Preparing the songs…";
            Task.Run(() =>
            {
                Directory.CreateDirectory(temp);
                try
                {
                    var files = burn.WriteTracks(temp, Decode, s => BeginInvoke(() => runningText = s), withGaps: false);
                    Burner.BurnStandard(drive.Id, files, ejectAfter, s => BeginInvoke(() => runningText = s));
                }
                finally { try { Directory.Delete(temp, true); } catch { } }
            }).ContinueWith(t => BeginInvoke(() =>
            {
                if (t.IsFaulted) Finish("Burn failed: " + (t.Exception.InnerException?.Message ?? t.Exception.Message), false);
                else Finish($"Burn complete in {SongCore.Short((DateTime.Now - started).TotalSeconds)} (standard 2-second gaps).", true);
            }));
        }

        protected override void Dispose(bool disposing) { if (disposing) { timer.Dispose(); player.Dispose(); } base.Dispose(disposing); }
    }
}
