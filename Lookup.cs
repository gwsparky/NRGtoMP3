using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NrgToMp3Win
{
    /// <summary>Find album and song names on MusicBrainz and apply them to a disc.</summary>
    class LookupForm : Form
    {
        readonly DiscItem disc;
        readonly TextBox album = new() { Width = 260 }, artist = new() { Width = 200, PlaceholderText = "Artist (optional)" };
        readonly ListView results = new() { View = View.Details, FullRowSelect = true, MultiSelect = false, Dock = DockStyle.Fill, HideSelection = false };
        readonly ListBox songs = new() { Dock = DockStyle.Fill, IntegralHeight = false };
        readonly Label status = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
        readonly Label songsHeader = new() { Dock = DockStyle.Top, Height = 40 };
        readonly Button use = new() { Text = "Use These Names", AutoSize = true, Enabled = false };
        (string album, string artist, List<string> titles)? preview;

        public LookupForm(DiscItem d)
        {
            disc = d;
            Text = "Look Up Song Names";
            ClientSize = new Size(860, 560);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9.5f);
            MinimizeBox = MaximizeBox = false;
            album.Text = d.Album;
            artist.Text = d.Artist;
            album.PlaceholderText = "Album";

            var search = new Button { Text = "Search", AutoSize = true };
            search.Click += async (_, _) => await DoSearch();
            album.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await DoSearch(); } };
            artist.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await DoSearch(); } };
            var match = new Button { Text = "Match Disc", AutoSize = true, Enabled = d.Toc != null };
            match.Click += async (_, _) => await DoMatch();

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8), WrapContents = true };
            top.Controls.Add(new Label { Text = $"{System.IO.Path.GetFileName(d.Path)} · {d.Songs.Count} songs", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 3, 3, 8) });
            top.SetFlowBreak(top.Controls[0], true);
            top.Controls.AddRange(new Control[] { album, artist, search, match });

            results.Columns.Add("Album", 230); results.Columns.Add("Artist", 150); results.Columns.Add("Date", 80);
            results.Columns.Add("Country", 60); results.Columns.Add("Songs", 55);
            results.SelectedIndexChanged += async (_, _) => await LoadPreview();

            var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 540 };
            split.Panel1.Controls.Add(results);
            split.Panel2.Controls.Add(songs);
            split.Panel2.Controls.Add(songsHeader);

            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            use.Click += (_, _) => Apply();
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
            bottom.Controls.AddRange(new Control[] { use, cancel, status });
            var credit = new Label { Text = "Song names come from MusicBrainz.org, a free community database.", Dock = DockStyle.Bottom, Height = 20, ForeColor = SystemColors.GrayText, Padding = new Padding(8, 0, 0, 0) };

            Controls.Add(split);
            Controls.Add(top);
            Controls.Add(credit);
            Controls.Add(bottom);
            AcceptButton = use;
            CancelButton = cancel;
            Shown += async (_, _) => { if (d.Toc?.Exact == true) await DoMatch(); };
            Theme.Apply(this);
            Dpi.Scale(this);
        }

        async Task Run(string message, Func<Task> work)
        {
            status.Text = message;
            UseWaitCursor = true;
            try { await work(); }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { UseWaitCursor = false; }
        }

        void Show(List<Release> found)
        {
            results.Items.Clear();
            songs.Items.Clear(); songsHeader.Text = ""; preview = null; use.Enabled = false;
            foreach (var r in found.OrderBy(r => r.TrackCount == disc.Songs.Count ? 0 : 1))
            {
                var item = new ListViewItem(new[] { r.Title, r.Artist, r.Date, r.Country, r.TrackCount.ToString() }) { Tag = r };
                if (r.TrackCount == disc.Songs.Count) item.ForeColor = Color.DarkGreen;
                results.Items.Add(item);
            }
            if (results.Items.Count == 1) results.Items[0].Selected = true;
        }

        Task DoMatch() => Run("Looking for this disc…", async () =>
        {
            var found = await MusicBrainz.Lookup(disc.Toc);
            Show(found);
            status.Text = found.Count > 0 ? $"Found {found.Count} matching release(s)."
                : disc.Toc.Exact ? "This disc isn't in MusicBrainz. Try searching by album or artist."
                : "No match. This file's song breaks were guessed from silences, so search by album or artist instead.";
        });

        Task DoSearch() => Run("Searching…", async () =>
        {
            var found = await MusicBrainz.Search(album.Text.Trim(), artist.Text.Trim());
            Show(found);
            status.Text = found.Count == 0 ? "Nothing found. Try fewer or different words."
                : $"{found.Count} result(s). Green ones have {disc.Songs.Count} songs, like your disc.";
        });

        async Task LoadPreview()
        {
            if (results.SelectedItems.Count == 0) return;
            var r = (Release)results.SelectedItems[0].Tag;
            await Run("Loading songs…", async () =>
            {
                var t = await MusicBrainz.Tracks(r.Id, disc.Songs.Count);
                if (results.SelectedItems.Count == 0 || !ReferenceEquals(results.SelectedItems[0].Tag, r)) return;
                preview = t;
                songs.Items.Clear();
                for (int i = 0; i < t.titles.Count; i++) songs.Items.Add($"{i + 1:00}  {t.titles[i]}");
                songsHeader.Text = $"{t.album}\n{t.artist}" + (t.titles.Count != disc.Songs.Count ? $"   ⚠ {t.titles.Count} songs (yours has {disc.Songs.Count}); applied in order" : "");
                use.Enabled = true;
                status.Text = "";
            });
        }

        void Apply()
        {
            if (preview is not { } p) return;
            disc.Album = p.album;
            disc.Artist = p.artist;
            for (int i = 0; i < disc.Songs.Count && i < p.titles.Count; i++)
                if (p.titles[i] != "") disc.Names[disc.Songs[i].Number] = p.titles[i];
            DialogResult = DialogResult.OK;
        }
    }
}
