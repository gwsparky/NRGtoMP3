using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace NrgToMp3Win
{
    /// <summary>Break one song into several tracks at chosen points, then save the parts as files.</summary>
    class SplitTab : UserControl
    {
        SplitPlan plan;
        BurnItem fromBurnList;            // the song came from the Burn CD list (✂): its parts replace it there
        readonly Button toBurn = new() { Text = "Send Parts to Burn List", AutoSize = true };

        /// <summary>Raised after parts replace a song on the burn list, to go back to the Burn CD tab.</summary>
        public event Action BackToBurnList;
        (float[] l, float[] r) peaks;
        readonly SongOpener opener = new("split it") { Dock = DockStyle.Fill };
        readonly Panel editor = new() { Dock = DockStyle.Fill, Visible = false };
        readonly Label title = new() { AutoSize = true, Font = new Font("Segoe UI", 11f, FontStyle.Bold), Margin = new Padding(3, 6, 3, 3) };
        readonly Button openButton = new() { Text = "Open Song…", AutoSize = true }, closeButton = new() { Text = "Close", AutoSize = true, Visible = false };
        readonly Button listButton = new() { Text = "◀ Song List", AutoSize = true, Visible = false };
        readonly WaveformView wave = new() { Dock = DockStyle.Top, Height = Dpi.Fit(200, 120), Markers = new() };
        readonly Button play = new() { Text = "▶ Play", Width = 90 };
        readonly Label time = new() { AutoSize = true, Font = new Font("Segoe UI", 12f), Margin = new Padding(8, 4, 8, 3) };
        readonly CheckBox stereo = new() { Text = "Show L / R", AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
        readonly TextBox typed = new() { Width = 80, PlaceholderText = "1:23.45" };
        readonly Label problem = new() { AutoSize = true, ForeColor = Color.Firebrick, Margin = new Padding(3, 7, 3, 3) };
        readonly DataGridView grid = new()
        {
            Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
            RowHeadersVisible = false, BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.None,
            EditMode = DataGridViewEditMode.EditOnEnter, SelectionMode = DataGridViewSelectionMode.CellSelect,
        };
        readonly Label partsHeader = new() { Dock = DockStyle.Top, Height = 26, ForeColor = SystemColors.GrayText, Padding = new Padding(0, 6, 0, 0) };
        readonly ExportBar export = new("splitFolder", "Export Parts as") { Dock = DockStyle.Bottom };
        readonly SongPlayer player = new();
        readonly Timer timer = new() { Interval = 50 };
        bool building;

        public SplitTab()
        {
            openButton.Click += (_, _) => opener.Browse();
            closeButton.Click += (_, _) => Close();
            listButton.Click += (_, _) => BackToList();
            new ToolTip().SetToolTip(listButton, "Back to the list of songs on the disc");
            var header = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            header.Controls.AddRange(new Control[] { title, listButton, openButton, closeButton });
            opener.Loaded += LoadSong;

            // transport and split controls
            play.Click += (_, _) => player.Toggle();
            var back = new Button { Text = "−5 s", AutoSize = true }; back.Click += (_, _) => player.Seek(player.Position - 5);
            var fwd = new Button { Text = "+5 s", AutoSize = true }; fwd.Click += (_, _) => player.Seek(player.Position + 5);
            stereo.Checked = Settings.Get("stereoWaveform", "true") == "true";
            stereo.CheckedChanged += (_, _) => { Settings.Set("stereoWaveform", stereo.Checked); wave.Stereo = stereo.Checked; wave.Invalidate(); };
            var add = new Button { Text = "Add", AutoSize = true }; add.Click += (_, _) => AddTyped();
            typed.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AddTyped(); } };
            var list = new Button { Text = "Enter Times…", AutoSize = true }; list.Click += (_, _) => EnterTimes();
            var here = new Button { Text = "✂ Add Split Here (Ctrl+K)", AutoSize = true, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
            here.Click += (_, _) => AddHere();
            var transport = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(0, 4, 0, 0) };
            transport.Controls.AddRange(new Control[] { play, back, fwd, time, stereo, new Label { Text = "   Split at", AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, typed, add, list, here, problem });

            wave.SeekRequested += t => { player.Seek(t); wave.Position = t; wave.Invalidate(); };
            wave.MarkerMoved += (i, t) => { plan.Move(i, t); Changed(); };

            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "num", HeaderText = "#", Width = 36, ReadOnly = true });
            grid.Columns.Add(new DataGridViewButtonColumn { Name = "play", HeaderText = "", Width = 36, Text = "▶", UseColumnTextForButtonValue = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "Name (click to edit)", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            foreach (var (n, t, tip) in new[] { ("m1", "−1s", "Start 1 s earlier"), ("m01", "−0.1", "Start 0.1 s earlier") })
                grid.Columns.Add(new DataGridViewButtonColumn { Name = n, HeaderText = "", Width = 44, Text = t, UseColumnTextForButtonValue = true, ToolTipText = tip });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "start", HeaderText = "Starts at", Width = 86, ToolTipText = "Type a time like 1:23.45 and press Enter" });
            foreach (var (n, t, tip) in new[] { ("p01", "+0.1", "Start 0.1 s later"), ("p1", "+1s", "Start 1 s later"), ("ear", "👂", "Hear 3 seconds either side of this split point") })
                grid.Columns.Add(new DataGridViewButtonColumn { Name = n, HeaderText = "", Width = 44, Text = t, UseColumnTextForButtonValue = true, ToolTipText = tip });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "end", HeaderText = "Ends at", Width = 80, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "len", HeaderText = "Length", Width = 60, ReadOnly = true });
            grid.Columns.Add(new DataGridViewButtonColumn { Name = "remove", HeaderText = "", Width = 36, Text = "✕", UseColumnTextForButtonValue = true, ToolTipText = "Remove this split point (joins this part to the one before)" });
            grid.CellContentClick += GridClick;
            grid.CellEndEdit += GridEdited;

            var gridPanel = new Panel { Dock = DockStyle.Fill };
            gridPanel.Controls.Add(grid); gridPanel.Controls.Add(partsHeader);
            editor.Controls.Add(gridPanel); editor.Controls.Add(transport); editor.Controls.Add(wave);

            export.ExportButton.Click += (_, _) => Export();
            toBurn.Click += (_, _) => SendToBurnList();
            new ToolTip().SetToolTip(toBurn, "Put the parts on the Burn CD list as separate tracks with no gap between them");
            export.AddButton(toBurn);
            export.Visible = false;

            Controls.Add(editor); Controls.Add(opener); Controls.Add(export); Controls.Add(header);
            player.Changed += () => play.Text = player.Playing ? "⏸ Pause" : "▶ Play";
            timer.Tick += (_, _) => { player.Tick(); if (plan != null) { wave.Position = player.Position; wave.Invalidate(); time.Text = $"{SongCore.Precise(player.Position)} / {SongCore.Precise(plan.Duration)}"; } };
            timer.Start();
            VisibleChanged += (_, _) => { if (!Visible) player.Pause(); };
            ShowState();
        }

        void LoadSong(string name, byte[] pcm) { fromBurnList = null; Show(name, pcm); }

        /// <summary>Opens a song from the Burn CD list (its ✂ button).</summary>
        public void OpenBurnItem(BurnItem item)
        {
            player.Load(null); plan = null; fromBurnList = null;
            opener.ShowIdle(); ShowState();
            title.Text = $"✂ Reading “{item.Title}”…";
            System.Threading.Tasks.Task.Run(() => item.Load(SongFiles.Decode)).ContinueWith(t => BeginInvoke(() =>
            {
                if (t.IsFaulted) { title.Text = "✂ Couldn't read the song: " + t.Exception.InnerException.Message; return; }
                fromBurnList = item;
                Show(item.Title, t.Result);
            }));
        }

        void Show(string name, byte[] pcm)
        {
            plan = new SplitPlan(name, pcm);
            peaks = SongCore.Peaks(pcm);
            wave.LeftPeaks = peaks.l; wave.RightPeaks = peaks.r; wave.Duration = plan.Duration; wave.Markers = plan.Markers; wave.Stereo = stereo.Checked;
            player.Load(pcm);
            problem.Text = ""; export.Status.Text = "";
            ShowState();
            Changed();
        }

        void Close() { player.Load(null); plan = null; fromBurnList = null; opener.ShowIdle(); ShowState(); }

        void BackToList() { player.Load(null); plan = null; fromBurnList = null; opener.ShowList(); ShowState(); }

        /// <summary>The parts, one after another with no gap, onto the burn list (in place of the song if it came from there).</summary>
        void SendToBurnList()
        {
            if (plan == null || plan.Markers.Count == 0) return;
            if (grid.IsCurrentCellInEditMode) grid.EndEdit();
            player.Pause();
            var parts = plan.MakeParts().Select((p, k) => new BurnItem
            {
                Title = p.title, Pcm = p.pcm, Seconds = SongCore.Seconds(p.pcm), Note = $"Part {k + 1} of {plan.Title}", Gap = k == 0 ? fromBurnList?.Gap : 0,
            }).ToList();
            var list = BurnList.Shared;
            int at = fromBurnList == null ? -1 : list.Items.IndexOf(fromBurnList);
            if (at >= 0) { list.Items.RemoveAt(at); list.Items.InsertRange(at, parts); list.NotifyChanged(); }
            else list.Add(parts);
            export.Status.Text = at >= 0 ? $"Replaced “{plan.Title}” on the Burn CD list with {parts.Count} parts." : $"Added {parts.Count} parts to the Burn CD list.";
            if (at >= 0) { Close(); BackToBurnList?.Invoke(); }
        }

        void ShowState()
        {
            bool open = plan != null;
            editor.Visible = export.Visible = closeButton.Visible = open;
            listButton.Visible = open && opener.HasList;
            opener.Visible = !open;
            title.Text = open ? $"✂ {plan.Title} · {SongCore.Precise(plan.Duration)}" : "✂ Split a song into separate tracks";
        }

        /// <summary>Redraws the waveform and parts list after the split points change.</summary>
        void Changed()
        {
            if (plan == null) return;
            wave.Invalidate();
            partsHeader.Text = plan.Markers.Count == 0
                ? "No split points yet. Play the song and press Add Split Here (Ctrl+K) where each new track should start, or type times."
                : $"{plan.Parts} tracks. They follow each other with no gap, so the music plays straight through.";
            export.SetLabel(plan.Markers.Count == 0 ? "Export Parts as" : $"Export {plan.Parts} Parts as");
            export.ExportButton.Enabled = plan.Markers.Count > 0 && !export.Running;
            toBurn.Enabled = plan.Markers.Count > 0;
            toBurn.Text = fromBurnList != null ? "Replace on Burn List" : "Send Parts to Burn List";
            if (grid.IsCurrentCellInEditMode) return;   // don't rebuild under someone typing
            building = true;
            grid.Rows.Clear();
            for (int k = 0; k < plan.Parts; k++)
            {
                int r = grid.Rows.Add(k + 1, null, plan.Names[k] ?? "", null, null, SongCore.Precise(plan.Start(k)), null, null, null,
                                      SongCore.Precise(plan.End(k)), SongCore.Short(plan.End(k) - plan.Start(k)), null);
                var row = grid.Rows[r];
                row.Cells["name"].ToolTipText = plan.DefaultName(k);
                if (string.IsNullOrEmpty(plan.Names[k])) row.Cells["name"].Style.ForeColor = SystemColors.GrayText;
                if (string.IsNullOrEmpty(plan.Names[k])) row.Cells["name"].Value = plan.DefaultName(k);
                if (k == 0)   // the first part always starts at 0:00
                {
                    row.Cells["start"].ReadOnly = true;
                    foreach (var c in new[] { "m1", "m01", "p01", "p1", "ear", "remove" })
                        row.Cells[c] = new DataGridViewTextBoxCell { Value = "" };
                    row.Cells["start"].Style.ForeColor = SystemColors.GrayText;
                }
            }
            building = false;
        }

        bool AddAt(double t, string what)
        {
            if (plan == null) return false;
            if (t <= 0 || t >= plan.Duration) { problem.Text = $"{what} is outside the song (it's {SongCore.Precise(plan.Duration)} long)."; return false; }
            if (!plan.Add(t)) { problem.Text = $"{SongCore.Precise(t)} is too close to the start, the end, or another split point."; return false; }
            problem.Text = "";
            Changed();
            return true;
        }

        void AddHere() { if (plan != null) AddAt(player.Position, SongCore.Precise(player.Position)); }

        void AddTyped()
        {
            if (SongCore.ParseTime(typed.Text) is not double t) { problem.Text = "Type a time like 1:23.45 or 83.5"; return; }
            if (AddAt(t, typed.Text)) typed.Text = "";
        }

        void EnterTimes()
        {
            if (plan == null) return;
            using var f = new TimeListForm(plan.Duration);
            if (f.ShowDialog(this) != DialogResult.OK) return;
            var (added, skipped) = plan.Apply(f.Entries, f.Replace);
            problem.Text = skipped.Count == 0 ? ""
                : $"Made {added} split point(s); skipped {string.Join(", ", skipped.Select(SongCore.Precise))} (past the end or too close to another split).";
            Changed();
        }

        void GridClick(object sender, DataGridViewCellEventArgs e)
        {
            if (plan == null || e.RowIndex < 0 || e.RowIndex >= plan.Parts) return;
            int k = e.RowIndex, i = k - 1;   // part k starts at split point k-1
            switch (grid.Columns[e.ColumnIndex].Name)
            {
                case "play": player.Play(plan.Start(k), plan.End(k)); return;
                case "ear" when k > 0: player.Play(Math.Max(0, plan.Markers[i] - 3), plan.Markers[i] + 3); return;
                case "m1" when k > 0: plan.Move(i, plan.Markers[i] - 1); break;
                case "m01" when k > 0: plan.Move(i, plan.Markers[i] - 0.1); break;
                case "p01" when k > 0: plan.Move(i, plan.Markers[i] + 0.1); break;
                case "p1" when k > 0: plan.Move(i, plan.Markers[i] + 1); break;
                case "remove" when k > 0: plan.Remove(i); break;
                default: return;
            }
            BeginInvoke(Changed);   // rebuild the list after this click has finished
        }

        void GridEdited(object sender, DataGridViewCellEventArgs e)
        {
            if (building || plan == null || e.RowIndex < 0 || e.RowIndex >= plan.Parts) return;
            int k = e.RowIndex;
            var value = grid.Rows[k].Cells[e.ColumnIndex].Value as string ?? "";
            switch (grid.Columns[e.ColumnIndex].Name)
            {
                case "name":
                    plan.Names[k] = value.Trim() == plan.DefaultName(k) ? null : value;
                    break;
                case "start" when k > 0:
                    if (SongCore.ParseTime(value) is double t) plan.Move(k - 1, t);
                    else problem.Text = "Type a start time like 1:23.45";
                    break;
            }
            BeginInvoke(Changed);
        }

        void Export()
        {
            if (plan == null || export.Folder == null || plan.Markers.Count == 0) { if (export.Folder == null) export.Status.Text = "Choose a folder to save to first."; return; }
            player.Pause();
            var parts = plan.MakeParts();
            string album = plan.Title;
            bool wav = export.Wav, numbered = Settings.GetBool("numberNames");
            string folder = export.Folder;
            export.Run(progress =>
            {
                SongCore.Export(parts, album, folder, wav, numbered);
                return $"Saved {parts.Count} parts.";
            });
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (plan != null && keyData == (Keys.Control | Keys.K)) { AddHere(); return true; }
            if (plan != null && keyData == (Keys.Control | Keys.P)) { player.Toggle(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void Dispose(bool disposing) { if (disposing) { timer.Dispose(); player.Dispose(); } base.Dispose(disposing); }
    }

    /// <summary>Type or paste a list of times, one per line, each optionally followed by a name.</summary>
    class TimeListForm : Form
    {
        readonly TextBox text = new() { Multiline = true, Dock = DockStyle.Fill, Font = new Font("Consolas", 10f), ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
        readonly RadioButton starts = new() { Text = "Start times (when each part begins)", AutoSize = true, Checked = true };
        readonly RadioButton lengths = new() { Text = "Lengths (how long each part lasts)", AutoSize = true };
        readonly CheckBox replace = new() { Text = "Replace the split points already there", AutoSize = true, Checked = true };
        readonly Label preview = new() { Dock = DockStyle.Bottom, Height = 40, ForeColor = SystemColors.GrayText };
        readonly double duration;
        public List<(double start, string name)> Entries => SongCore.ParseTimeList(text.Text, lengths.Checked);
        public bool Replace => replace.Checked;

        public TimeListForm(double duration)
        {
            this.duration = duration;
            Text = "Enter Split Times";
            ClientSize = new Size(560, 480);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = MaximizeBox = false;
            Font = new Font("Segoe UI", 9.5f);
            text.PlaceholderText = "0:00 Introduction\r\n1:32 Verse 2\r\n3:10.50 Chorus";
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown };
            top.Controls.Add(new Label { Text = $"One part per line: a time, then an optional name. Song length: {SongCore.Precise(duration)}.", AutoSize = true });
            top.Controls.AddRange(new Control[] { starts, lengths });
            var ok = new Button { Text = "Make Split Points", AutoSize = true, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft };
            bottom.Controls.AddRange(new Control[] { ok, cancel, replace });
            Controls.Add(text); Controls.Add(preview); Controls.Add(top); Controls.Add(bottom);
            AcceptButton = null;   // Enter makes new lines in the text box
            CancelButton = cancel;
            void Update()
            {
                var e = Entries;
                var inside = e.Where(x => x.start > 0.001 && x.start < duration).ToList();
                preview.Text = e.Count == 0 ? "No times recognised yet."
                    : $"{e.Count} line(s) → {inside.Count} split point(s): " + string.Join(", ", inside.Take(6).Select(x => SongCore.Precise(x.start)))
                      + (inside.Count > 6 ? "…" : "") + (e.Any(x => x.start >= duration) ? "  (times past the end will be skipped)" : "");
                ok.Enabled = e.Count > 0;
            }
            Theme.Apply(this);
            Dpi.Scale(this);
            text.TextChanged += (_, _) => Update();
            starts.CheckedChanged += (_, _) => Update();
            Update();
        }
    }
}
