using System;
using System.Drawing;
using System.Windows.Forms;

namespace NrgToMp3Win
{
    /// <summary>The GitHub names settings, remembered in settings.json (the access key scrambled with Windows' protection).</summary>
    static class SyncSettings
    {
        public static bool On => Settings.Get("githubSync", "true") == "true";
        public static string Repo => Settings.Get("githubRepo", NamesSync.DefaultRepo);
        public static string Token => Settings.GetSecret("githubToken");
    }

    /// <summary>"GitHub…": turn names syncing on or off, choose the repository, and paste the access key.</summary>
    class NamesSyncForm : Form
    {
        public NamesSyncForm()
        {
            Text = "Song Names on GitHub";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12);

            var on = new CheckBox { Text = "Keep song names in sync with GitHub", AutoSize = true, Checked = SyncSettings.On };
            var repo = new TextBox { Width = 320, Text = SyncSettings.Repo };
            var token = new TextBox { Width = 320, UseSystemPasswordChar = true, Text = SyncSettings.Token ?? "" };
            var help = new Label
            {
                AutoSize = true, MaximumSize = new Size(460, 0), ForeColor = SystemColors.GrayText,
                Text = "Names are kept in the repository's \"names\" folder, one file per disc (\"names/Church Music.txt\"). " +
                       "When you open a disc, names edited on github.com are loaded. Save Names also uploads them.\r\n\r\n" +
                       "Downloading works without a key. To upload, make a key on github.com: Settings → Developer settings → " +
                       "Personal access tokens → Fine-grained tokens → Generate new token; choose only this repository and " +
                       "Repository permissions → Contents: Read and write. Paste it below.",
            };
            var keyState = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 8, 3, 3) };
            void ShowKeyState() => keyState.Text = SyncSettings.Token != null ? "An access key is saved on this PC." : "No access key is saved.";
            ShowKeyState();
            var remove = new Button { Text = "Remove Key…", AutoSize = true };
            remove.Click += (_, _) =>
            {
                if (MessageBox.Show(this, "Remove the saved access key?\n\nNames will still download from GitHub. To upload them again, paste a new key here.",
                                    "Remove Key", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                Settings.SetSecret("githubToken", null);
                token.Text = "";
                ShowKeyState();
            };
            new ToolTip().SetToolTip(remove, "Delete the saved access key from this PC, e.g. if a wrong one was pasted");
            var ok = new Button { Text = "OK", AutoSize = true, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            AcceptButton = ok; CancelButton = cancel;
            ok.Click += (_, _) =>
            {
                Settings.Set("githubSync", on.Checked);
                Settings.Set("githubRepo", string.IsNullOrWhiteSpace(repo.Text) ? null : repo.Text.Trim());
                Settings.SetSecret("githubToken", token.Text);
            };

            var table = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
            table.Controls.Add(on); table.SetColumnSpan(on, 2);
            table.Controls.Add(help); table.SetColumnSpan(help, 2);
            table.Controls.Add(new Label { Text = "Repository", AutoSize = true, Margin = new Padding(3, 7, 3, 3) }); table.Controls.Add(repo);
            table.Controls.Add(new Label { Text = "Access key", AutoSize = true, Margin = new Padding(3, 7, 3, 3) }); table.Controls.Add(token);
            var keyRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            keyRow.Controls.AddRange(new Control[] { keyState, remove });
            table.Controls.Add(keyRow); table.SetColumnSpan(keyRow, 2);
            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
            buttons.Controls.AddRange(new Control[] { cancel, ok });
            table.Controls.Add(buttons); table.SetColumnSpan(buttons, 2);
            Controls.Add(table);
            Theme.Apply(this);
            Dpi.Scale(this);
        }
    }
}
