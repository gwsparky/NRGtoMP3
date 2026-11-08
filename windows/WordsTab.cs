using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NrgToMp3Win
{
    /// <summary>Song Words tab: Whisper listens to a song and writes down the words, which can be corrected and saved as a text file.</summary>
    class WordsTab : UserControl
    {
        string songTitle;
        byte[] pcm;
        bool working;
        DateTime started;
        readonly SongOpener opener = new("write down its words") { Dock = DockStyle.Fill };
        readonly Panel editor = new() { Dock = DockStyle.Fill, Visible = false };
        readonly Label title = new() { AutoSize = true, Font = new Font("Segoe UI", 11f, FontStyle.Bold), Margin = new Padding(3, 6, 3, 3), Text = "Write down the words of a song" };
        readonly Button openButton = new() { Text = "Open Song…", AutoSize = true }, cdButton = new() { Text = "Open CD", AutoSize = true };
        readonly Button listButton = new() { Text = "◀ Song List", AutoSize = true, Visible = false }, closeButton = new() { Text = "Close", AutoSize = true, Visible = false };
        readonly ComboBox side = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
        readonly Button getWords = new() { Text = "Get Words", AutoSize = true, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
        readonly Button copy = new() { Text = "Copy", AutoSize = true }, save = new() { Text = "Save Words…", AutoSize = true };
        readonly ProgressBar progress = new() { Width = 220, Visible = false, Margin = new Padding(3, 8, 3, 3) };
        readonly Label status = new() { AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
        readonly TextBox words = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, Font = new Font("Segoe UI", 11f) };
        readonly Timer timer = new() { Interval = 500 };
        string statusText = "";

        public WordsTab()
        {
            openButton.Click += (_, _) => opener.Browse();
            cdButton.Click += (_, _) => { Unload(); opener.OpenCd(); };
            listButton.Click += (_, _) => { Unload(); opener.ShowList(); };
            closeButton.Click += (_, _) => { Unload(); opener.ShowIdle(); };
            var header = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            header.Controls.AddRange(new Control[] { title, listButton, openButton, cdButton, closeButton });
            opener.Loaded += (name, data) => { songTitle = name; pcm = data; words.Text = ""; SetStatus(""); ShowState(); };

            side.Items.AddRange(new object[] { "Automatic", "Both sides", "Left side", "Right side" }); side.SelectedIndex = 0;
            getWords.Click += async (_, _) => await GetWords();
            copy.Click += (_, _) => { if (words.Text != "") Clipboard.SetText(words.Text); };
            save.Click += (_, _) => Save();
            words.TextChanged += (_, _) => UpdateButtons();
            var tips = new ToolTip();
            tips.SetToolTip(side, "Automatic: if the singers are on one side only (as on split tracks), it finds that side");
            tips.SetToolTip(getWords, "Listen to the song and write down the words (takes about a minute; nothing is uploaded)");
            var row = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            row.Controls.AddRange(new Control[] { new Label { Text = "Listen to:", AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, side, getWords, copy, save });
            var statusRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            statusRow.Controls.AddRange(new Control[] { progress, status });
            var note = new Label
            {
                Dock = DockStyle.Bottom, AutoSize = true, ForeColor = SystemColors.GrayText, Padding = new Padding(0, 6, 0, 0),
                Text = "The words are worked out by a computer, so check them. Most worship songs are copyrighted: showing them on a screen or in print usually needs a licence such as CCLI."
            };
            editor.Controls.Add(words); editor.Controls.Add(note); editor.Controls.Add(statusRow); editor.Controls.Add(row);
            Controls.Add(editor); Controls.Add(opener); Controls.Add(header);

            timer.Tick += (_, _) => status.Text = $"{statusText}  ({(int)(DateTime.Now - started).TotalSeconds} s)";
            ShowState();
        }

        void Unload() { pcm = null; songTitle = null; words.Text = ""; SetStatus(""); ShowState(); }

        void ShowState()
        {
            bool open = pcm != null;
            editor.Visible = open; opener.Visible = !open;
            title.Text = open ? songTitle : "Write down the words of a song";
            closeButton.Visible = open;
            listButton.Visible = open && opener.HasList;
            UpdateButtons();
        }

        void UpdateButtons()
        {
            foreach (var b in new Control[] { openButton, cdButton, listButton, closeButton, side, getWords }) b.Enabled = !working;
            getWords.Text = words.Text == "" ? "Get Words" : "Get Words Again";
            copy.Enabled = words.Text != "";
            save.Enabled = words.Text != "" && !working;
        }

        void SetStatus(string s) { statusText = s; status.Text = s; }

        async Task GetWords()
        {
            if (pcm == null || working) return;
            working = true; UpdateButtons();
            try
            {
                if (!File.Exists(WordsCore.ModelPath))
                {
                    SetStatus("Downloading the speech model (about 490 MB, only needed once)…");
                    progress.Value = 0; progress.Visible = true;
                    await WordsCore.DownloadModel(new Progress<double>(p => progress.Value = (int)(p * 100)));
                    progress.Visible = false;
                }
                started = DateTime.Now; timer.Start();
                var data = pcm;
                var choice = (WordsSide)side.SelectedIndex;
                var (text, note) = await Task.Run(() => WordsCore.GetWords(data, choice, s => BeginInvoke(() => statusText = s)));
                timer.Stop();
                if (data != pcm) return;   // another song was opened meanwhile
                words.Text = text.Replace("\n", "\r\n");
                int n = WordsCore.WordCount(text);
                SetStatus(n == 0 ? "No words heard. Try another side under Listen to."
                                 : $"Heard {n} words{note}. Read them through and fix any mistakes, then save.");
            }
            catch (Exception e)
            {
                timer.Stop(); progress.Visible = false;
                SetStatus("Couldn't get the words: " + (e.InnerException ?? e).Message);
            }
            finally { working = false; UpdateButtons(); }
        }

        void Save()
        {
            var name = NrgToMp3.Program.Clean((songTitle ?? "Song") + " - Words") + ".txt";
            using var d = new SaveFileDialog { Filter = "Text file (*.txt)|*.txt", FileName = name, Title = "Save the words" };
            var folder = Settings.Get("wordsFolder");
            if (folder != null && Directory.Exists(folder)) d.InitialDirectory = folder;
            if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
            try
            {
                File.WriteAllText(d.FileName, words.Text.TrimEnd() + "\r\n", new UTF8Encoding(false));
                Settings.Set("wordsFolder", Path.GetDirectoryName(d.FileName));
                SetStatus("Saved " + Path.GetFileName(d.FileName));
            }
            catch (Exception e) { SetStatus("Couldn't save: " + e.Message); }
        }
    }
}
