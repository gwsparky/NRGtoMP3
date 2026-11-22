using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace NrgToMp3Win
{
    /// <summary>"Credits": the parts made by other people and their licences (the Licenses folder next to the .exe).</summary>
    static class Credits
    {
        static string Folder => Path.Combine(AppContext.BaseDirectory, "Licenses");

        public static void Show(IWin32Window owner)
        {
            string file = Path.Combine(Folder, "Credits.txt");
            string text = File.Exists(file) ? File.ReadAllText(file).Replace("\r\n", "\n").Replace("\n", "\r\n")
                : "The Licenses folder is missing. It belongs next to the program's .exe.";
            using var f = new Form { Text = "Credits and Licences", StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(760, 480), MinimizeBox = false };
            var box = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Text = text, Font = new Font("Consolas", 9f), Location = new Point(10, 10), Size = new Size(740, 420), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            var open = new Button { Text = "Show Licence Files", AutoSize = true, Location = new Point(10, 440), Anchor = AnchorStyles.Bottom | AnchorStyles.Left, Enabled = Directory.Exists(Folder) };
            open.Click += (_, _) => Process.Start(new ProcessStartInfo(Folder) { UseShellExecute = true });
            var close = new Button { Text = "Close", AutoSize = true, Location = new Point(670, 440), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, DialogResult = DialogResult.OK };
            f.Controls.AddRange(new Control[] { box, open, close });
            f.AcceptButton = close; f.CancelButton = close;
            box.Select(0, 0);
            Theme.Apply(f); Dpi.Scale(f);
            f.ShowDialog(owner);
        }
    }
}
