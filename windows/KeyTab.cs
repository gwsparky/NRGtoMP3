using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NrgToMp3Win
{
    /// <summary>Change a song's key by half steps (speed unchanged), fine tune it, and optionally remove one side.</summary>
    class KeyTab : UserControl
    {
        static readonly string[] KeyNames = { "C", "C♯/D♭", "D", "E♭", "E", "F", "F♯/G♭", "G", "A♭", "A", "B♭", "B" };
        static readonly string[] FileKeyNames = { "C", "C#", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B" };

        string songTitle;
        byte[] source;                    // as loaded
        byte[] changed;                   // with the chosen side removed
        (float[] l, float[] r) sourcePeaks, changedPeaks;
        int halfSteps, cents;
        readonly SongOpener opener = new("change its key") { Dock = DockStyle.Fill };
        readonly Panel editor = new() { Dock = DockStyle.Fill, Visible = false };
        readonly Label title = new() { AutoSize = true, Font = new Font("Segoe UI", 11f, FontStyle.Bold), Margin = new Padding(3, 6, 3, 3) };
        readonly Button openButton = new() { Text = "Open Song…", AutoSize = true }, closeButton = new() { Text = "Close", AutoSize = true, Visible = false };
        readonly Button listButton = new() { Text = "◀ Song List", AutoSize = true, Visible = false };
        readonly Label steps = new() { AutoSize = false, Width = 170, Height = 70, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 34f, FontStyle.Bold) };
        readonly Label stepsText = new() { AutoSize = false, Width = 170, TextAlign = ContentAlignment.TopCenter, ForeColor = SystemColors.GrayText };
        readonly Button down = new() { Text = "−", Width = 56, Height = 48, Font = new Font("Segoe UI", 18f, FontStyle.Bold) };
        readonly Button up = new() { Text = "+", Width = 56, Height = 48, Font = new Font("Segoe UI", 18f, FontStyle.Bold) };
        readonly Button reset = new() { Text = "Reset", AutoSize = true };
        readonly ComboBox originalKey = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
        readonly Label newKey = new() { AutoSize = true, Font = new Font("Segoe UI", 16f, FontStyle.Bold) };
        readonly TrackBar fine = new() { Minimum = -50, Maximum = 50, Width = 260, TickFrequency = 10 };
        readonly Label fineText = new() { AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
        readonly ComboBox channels = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
        readonly ComboBox fill = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
        readonly Label warning = new() { AutoSize = true, ForeColor = Color.DarkOrange, Margin = new Padding(3, 8, 3, 3) };
        readonly WaveformView wave = new() { Dock = DockStyle.Top, Height = 180 };
        readonly Label waveCaption = new() { Dock = DockStyle.Top, Height = 22, ForeColor = SystemColors.GrayText };
        readonly Button play = new() { Text = "▶ Play", Width = 90 };
        readonly Label time = new() { AutoSize = true, Font = new Font("Segoe UI", 11f), Margin = new Padding(8, 6, 8, 3) };
        readonly RadioButton hearNew = new() { Text = "New key", AutoSize = true, Checked = true, Margin = new Padding(3, 8, 3, 3) };
        readonly RadioButton hearOriginal = new() { Text = "Original (to compare)", AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
        readonly Label willBeNamed = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 8, 3, 3) };
        readonly ExportBar export = new("keyFolder", "Export") { Dock = DockStyle.Bottom };
        readonly SongPlayer player = new(livePitch: true);
        readonly Timer timer = new() { Interval = 100 };
        int rebuild;                      // counts channel changes, so an older background result is ignored

        public KeyTab()
        {
            openButton.Click += (_, _) => opener.Browse();
            closeButton.Click += (_, _) => Close();
            listButton.Click += (_, _) => BackToList();
            new ToolTip().SetToolTip(listButton, "Back to the list of songs on the disc");
            var header = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            header.Controls.AddRange(new Control[] { title, listButton, openButton, closeButton });
            opener.Loaded += LoadSong;

            down.Click += (_, _) => { if (halfSteps > -12) { halfSteps--; Changed(); } };
            up.Click += (_, _) => { if (halfSteps < 12) { halfSteps++; Changed(); } };
            reset.Click += (_, _) => { halfSteps = 0; cents = 0; fine.Value = 0; Changed(); };
            originalKey.Items.Add("Not set"); originalKey.Items.AddRange(KeyNames); originalKey.SelectedIndex = 0;
            originalKey.SelectedIndexChanged += (_, _) => Changed();
            fine.ValueChanged += (_, _) => { cents = fine.Value; Changed(); };
            channels.Items.AddRange(new object[] { "Keep both sides", "Remove left side", "Remove right side" }); channels.SelectedIndex = 0;
            channels.SelectedIndexChanged += (_, _) => { UpdateFillChoices(); RebuildChannels(); };
            fill.SelectedIndexChanged += (_, _) => { if (!fillingChoices) RebuildChannels(); };
            UpdateFillChoices();

            var stepBox = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Margin = new Padding(6, 0, 6, 0) };
            stepBox.Controls.Add(steps); stepBox.Controls.Add(stepsText);
            var keyBox = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Margin = new Padding(30, 6, 3, 3) };
            var keyRow = new FlowLayoutPanel { AutoSize = true };
            keyRow.Controls.AddRange(new Control[] { new Label { Text = "Original key:", AutoSize = true, Margin = new Padding(3, 7, 3, 3) }, originalKey });
            keyBox.Controls.AddRange(new Control[] { keyRow, newKey });
            var row1 = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            row1.Controls.AddRange(new Control[] { down, stepBox, up, reset, keyBox });
            foreach (Control c in new Control[] { down, up, reset }) c.Margin = new Padding(3, 20, 3, 3);

            var row2 = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            row2.Controls.AddRange(new Control[] { new Label { Text = "Fine tune:", AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, fine, fineText,
                new Label { Text = "for recordings slightly sharp or flat (100 cents = 1 half step)", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 8, 3, 3) } });
            var row3 = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            row3.Controls.AddRange(new Control[] { new Label { Text = "Channels:", AutoSize = true, Margin = new Padding(3, 7, 3, 3) }, channels, fill, warning });

            play.Click += (_, _) => player.Toggle();
            hearNew.CheckedChanged += (_, _) => UpdatePreview();
            var transport = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            transport.Controls.AddRange(new Control[] { play, time, hearNew, hearOriginal, willBeNamed });
            wave.SeekRequested += t => player.Seek(t);

            editor.Controls.Add(transport); editor.Controls.Add(waveCaption); editor.Controls.Add(wave);
            editor.Controls.Add(row3); editor.Controls.Add(row2); editor.Controls.Add(row1);

            export.ExportButton.Click += (_, _) => Export();
            var toBurn = new Button { Text = "Add to Burn List", AutoSize = true };
            toBurn.Click += (_, _) => AddToBurnList();
            new ToolTip().SetToolTip(toBurn, "Add the song in its new key to the list on the Burn CD tab");
            export.AddButton(toBurn);
            export.Visible = false;
            Controls.Add(editor); Controls.Add(opener); Controls.Add(export); Controls.Add(header);

            player.Changed += () => play.Text = player.Playing ? "⏸ Pause" : "▶ Play";
            timer.Tick += (_, _) =>
            {
                if (source == null) return;
                wave.Position = player.Position; wave.Invalidate();
                time.Text = $"{SongCore.Short(player.Position)} / {SongCore.Short(player.Duration)}";
            };
            timer.Start();
            VisibleChanged += (_, _) => { if (!Visible) player.Pause(); };
            ShowState();
            Changed();
        }

        double TotalSemitones => halfSteps + cents / 100.0;
        SongCore.Channels Mode => (SongCore.Channels)channels.SelectedIndex;
        bool Duplicate => fill.SelectedIndex != 1;

        void LoadSong(string name, byte[] pcm)
        {
            songTitle = name;
            source = changed = pcm;
            sourcePeaks = changedPeaks = SongCore.Peaks(pcm);
            lastLoaded = null;                       // a new song starts from the beginning
            wave.Duration = SongCore.Seconds(pcm);
            export.Status.Text = "";
            ShowState();
            if (Mode != SongCore.Channels.Both) RebuildChannels(); else UpdatePreview(reload: true);
            Changed();
        }

        void Close() { player.Load(null); source = changed = lastLoaded = null; songTitle = null; opener.ShowIdle(); ShowState(); }

        /// <summary>Back to the disc's song list; the key settings stay for the next song.</summary>
        void BackToList() { player.Load(null); source = changed = lastLoaded = null; songTitle = null; opener.ShowList(); ShowState(); }

        void ShowState()
        {
            bool open = source != null;
            editor.Visible = export.Visible = closeButton.Visible = open;
            listButton.Visible = open && opener.HasList;
            opener.Visible = !open;
            title.Text = open ? $"♪ {songTitle} · {SongCore.Short(SongCore.Seconds(source))} · speed stays the same" : "♪ Change the key of a song";
        }

        string ChannelNote => Mode switch
        {
            SongCore.Channels.RemoveRight => Duplicate ? "left side in both speakers" : "left side only",
            SongCore.Channels.RemoveLeft => Duplicate ? "right side in both speakers" : "right side only",
            _ => null,
        };

        /// <summary>"Amazing Grace (+2, key of A, left side only)"</summary>
        string NewTitle
        {
            get
            {
                var parts = new List<string>();
                if (halfSteps != 0) parts.Add(halfSteps > 0 ? $"+{halfSteps}" : $"-{-halfSteps}");
                if (cents != 0) parts.Add($"{(cents > 0 ? "+" : "")}{cents} cents");
                if (originalKey.SelectedIndex > 0) parts.Add("key of " + FileKeyNames[((originalKey.SelectedIndex - 1 + halfSteps) % 12 + 12) % 12]);
                if (ChannelNote is string c) parts.Add(c);
                return parts.Count == 0 ? songTitle : $"{songTitle} ({string.Join(", ", parts)})";
            }
        }

        void Changed()
        {
            steps.Text = halfSteps == 0 ? "0" : halfSteps > 0 ? $"+{halfSteps}" : $"−{-halfSteps}";
            stepsText.Text = halfSteps == 0 ? "Original key" : $"{Math.Abs(halfSteps)} half step{(Math.Abs(halfSteps) == 1 ? "" : "s")} {(halfSteps > 0 ? "up" : "down")}";
            down.Enabled = halfSteps > -12; up.Enabled = halfSteps < 12;
            fineText.Text = $"{(cents > 0 ? "+" : "")}{cents} cents";
            newKey.Text = originalKey.SelectedIndex > 0
                ? $"{KeyNames[originalKey.SelectedIndex - 1]}  →  {KeyNames[((originalKey.SelectedIndex - 1 + halfSteps) % 12 + 12) % 12]}"
                : "";
            fill.Visible = Mode != SongCore.Channels.Both;
            warning.Text = Math.Abs(halfSteps) > 5 ? "Big changes can sound less natural, especially for voices." : "";
            if (songTitle != null) willBeNamed.Text = $"Will be named “{NewTitle}”";
            UpdatePreview();
        }

        bool fillingChoices;

        /// <summary>The wording of "copy to both / leave silent" depends on which side is removed.</summary>
        void UpdateFillChoices()
        {
            fillingChoices = true;
            int keep = Math.Max(0, fill.SelectedIndex);
            string kept = Mode == SongCore.Channels.RemoveRight ? "left" : "right", gone = Mode == SongCore.Channels.RemoveRight ? "right" : "left";
            fill.Items.Clear();
            fill.Items.AddRange(new object[] { $"Copy the {kept} side to both speakers", $"Leave the {gone} side silent" });
            fill.SelectedIndex = keep;
            fillingChoices = false;
        }

        void RebuildChannels()
        {
            Changed();
            if (source == null) return;
            var mode = Mode; bool dup = Duplicate; var src = source; int me = ++rebuild;
            Task.Run(() =>
            {
                var output = SongCore.ApplyChannels(src, mode, dup);
                return (output, peaks: mode == SongCore.Channels.Both ? sourcePeaks : SongCore.Peaks(output));
            }).ContinueWith(t => BeginInvoke(() =>
            {
                if (me != rebuild || source != src) return;   // a newer choice is on its way
                changed = t.Result.output; changedPeaks = t.Result.peaks;
                UpdatePreview(reload: true);
            }));
        }

        /// <summary>Waveform and preview follow the "New key / Original" choice.</summary>
        void UpdatePreview(bool reload = false)
        {
            if (source == null) return;
            bool original = hearOriginal.Checked;
            var p = original ? sourcePeaks : changedPeaks;
            wave.LeftPeaks = p.l; wave.RightPeaks = p.r; wave.Invalidate();
            waveCaption.Text = original ? "Showing the original song (click to jump)"
                : Mode == SongCore.Channels.Both ? "Left (top) and right (bottom) channels (click to jump)"
                : $"Showing the song with the {(Mode == SongCore.Channels.RemoveRight ? "right" : "left")} side {(Duplicate ? "replaced by a copy of the other" : "removed")}";
            player.Semitones = original ? 0 : TotalSemitones;
            var want = original ? source : changed;
            if (reload || !ReferenceEquals(lastLoaded, want)) { player.Load(want, keepPlace: lastLoaded != null); lastLoaded = want; }
        }
        byte[] lastLoaded;

        void Export()
        {
            if (source == null) return;
            if (export.Folder == null) { export.Status.Text = "Choose a folder to save to first."; return; }
            player.Pause();
            var pcm = changed; double semis = TotalSemitones; string name = NewTitle;
            bool wav = export.Wav; string folder = export.Folder;
            export.Run(progress =>
            {
                var shifted = SongCore.ShiftPitch(pcm, semis, p => progress(p * 0.9));
                SongCore.Export(new[] { (name, shifted) }, name, folder, wav, numberNames: false);
                progress(1);
                return $"Saved “{name}”.";
            });
        }

        void AddToBurnList()
        {
            if (source == null || export.Running) return;
            player.Pause();
            var pcm = changed; double semis = TotalSemitones; string name = NewTitle;
            export.Run(progress =>
            {
                var shifted = SongCore.ShiftPitch(pcm, semis, progress);
                Invoke(() => BurnList.Shared.Add(new[] { new BurnItem { Title = name, Pcm = shifted, Seconds = SongCore.Seconds(shifted), Note = "Key change" } }));
                return $"Added “{name}” to the Burn CD list.";
            }, openFolder: false);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (source != null && keyData == (Keys.Control | Keys.P)) { player.Toggle(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void Dispose(bool disposing) { if (disposing) { timer.Dispose(); player.Dispose(); } base.Dispose(disposing); }
    }
}
