using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;
using NAudio.MediaFoundation;

namespace NrgToMp3Win
{
    static class App
    {
        [STAThread]
        static void Main()
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            MediaFoundationApi.Startup();       // Windows' own MP3 encoder
            NrgToMp3.Program.Mp3Encoder = Audio.EncodeMp3;
            Application.Run(new MainForm());
        }
    }

    /// <summary>Remembered choices, kept in %APPDATA%\NRG to MP3\settings.json.</summary>
    static class Settings
    {
        static readonly string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NRG to MP3", "settings.json");
        static Dictionary<string, string> values = Load();

        static Dictionary<string, string> Load()
        {
            try { return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? new(); }
            catch { return new(); }
        }

        public static string Get(string key, string fallback = null) => values.TryGetValue(key, out var v) ? v : fallback;
        public static bool GetBool(string key) => Get(key) == "true";

        public static void Set(string key, string value)
        {
            if (value == null) values.Remove(key); else values[key] = value;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file, JsonSerializer.Serialize(values));
            }
            catch { }
        }

        public static void Set(string key, bool value) => Set(key, value ? "true" : "false");
    }

    /// <summary>The layout is written for 100% display scaling (96 DPI). Text grows with Windows' scale setting
    /// by itself; this grows every size and position by the same amount (e.g. 2x at 200%), once, when a window opens.</summary>
    static class Dpi
    {
        public static void Scale(Form form)
        {
            form.AutoScaleMode = AutoScaleMode.None;
            form.HandleCreated += (_, _) =>
            {
                float f = form.DeviceDpi / 96f;
                if (Math.Abs(f - 1) > 0.01f) form.Scale(new SizeF(f, f));
            };
        }
    }

    class MainForm : Form
    {
        public const int Build = 13;   // shown in the title bar, to tell test versions apart

        public MainForm()
        {
            SuspendLayout();
            Text = $"NRG to MP3 (Windows test build {Build})";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            ClientSize = new Size(1100, 760);
            MinimumSize = new Size(900, 620);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9.5f);

            var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
            tabs.TabPages.Add(Page("Convert", new ConvertTab()));
            tabs.TabPages.Add(Page("Player", new PlayerTab()));
            foreach (var name in new[] { "Burn CD", "Copy CD" })
                tabs.TabPages.Add(Page(name, ComingSoon(name)));
            tabs.TabPages.Add(Page("Split Song", new SplitTab()));
            tabs.TabPages.Add(Page("Change Key", new KeyTab()));
            Controls.Add(tabs);

            // background colour: a slim bar above the tabs
            var pick = new Button { Text = "Background colour…", AutoSize = true };
            var reset = new Button { Text = "Default", AutoSize = true };
            pick.Click += (_, _) =>
            {
                using var d = new ColorDialog { FullOpen = true, Color = Theme.Background ?? SystemColors.Control };
                if (d.ShowDialog(this) == DialogResult.OK) { Theme.Set(this, d.Color); reset.Visible = true; }
            };
            reset.Click += (_, _) => { Theme.Set(this, null); reset.Visible = false; };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft };
            bar.Controls.AddRange(new Control[] { reset, pick });
            Controls.Add(bar);
            Theme.Load();
            reset.Visible = Theme.Background != null;
            Theme.Apply(this);
            Dpi.Scale(this);
            ResumeLayout(false);
            Load += (_, _) =>
            {
                // after scaling, the window may be bigger than the screen: fit it, or fill the screen
                var area = Screen.FromControl(this).WorkingArea;
                if (Width > area.Width || Height > area.Height)
                {
                    Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
                    Location = area.Location;
                    if (Width >= area.Width * 0.95) WindowState = FormWindowState.Maximized;
                }
            };
        }

        static TabPage Page(string title, Control content)
        {
            var page = new TabPage(title) { Padding = new Padding(10), UseVisualStyleBackColor = true };
            content.Dock = DockStyle.Fill;
            page.Controls.Add(content);
            return page;
        }

        static Control ComingSoon(string name) => new Label
        {
            Text = $"{name} is coming in the next stage of the Windows version.",
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = SystemColors.GrayText,
            Font = new Font("Segoe UI", 12f),
        };
    }
}
