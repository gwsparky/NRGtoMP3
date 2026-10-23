using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace NrgToMp3Win
{
    /// <summary>The optional background colour. Panels, labels and tabs take the colour itself; lists, log boxes
    /// and waveforms a light shade of it; text turns light on dark colours. Buttons, drop-downs and text you
    /// type into keep the normal Windows look so they stay recognisable.</summary>
    static class Theme
    {
        static Color? background;
        static readonly Dictionary<Control, (Color back, Color fore)> original = new();

        public static Color? Background => background;

        public static void Load()
        {
            var hex = Settings.Get("backgroundColor");
            if (hex != null && hex.Length == 6 && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int v))
                background = Color.FromArgb(v >> 16 & 0xFF, v >> 8 & 0xFF, v & 0xFF);
        }

        public static void Set(Control root, Color? colour)
        {
            background = colour;
            Settings.Set("backgroundColor", colour is Color c ? $"{c.R:X2}{c.G:X2}{c.B:X2}" : null);
            Apply(root);
        }

        static bool IsDark(Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B < 128;
        static Color Mix(Color a, Color b, double t) =>
            Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        /// <summary>Colours a control and everything inside it; also anything added to it later.</summary>
        public static void Apply(Control c)
        {
            if (!original.ContainsKey(c))
            {
                original[c] = (c.BackColor, c.ForeColor);
                c.ControlAdded += (_, e) => Apply(e.Control);
            }
            Paint(c);
            foreach (Control child in c.Controls) Apply(child);
        }

        static void Paint(Control c)
        {
            var (back0, fore0) = original[c];
            if (background is not Color bg)
            {
                c.BackColor = back0; c.ForeColor = fore0;
                if (c is TabPage tp) tp.UseVisualStyleBackColor = true;
                if (c is DataGridView g0) { g0.BackgroundColor = SystemColors.Window; g0.DefaultCellStyle.BackColor = SystemColors.Window; g0.DefaultCellStyle.ForeColor = SystemColors.ControlText; g0.EnableHeadersVisualStyles = true; }
                return;
            }
            bool dark = IsDark(bg);
            Color text = dark ? Color.White : Color.Black;
            Color tint = Mix(bg, text, 0.07);                  // lists, logs and waveforms: a light shade of the background
            // text: keep special colours (warnings, errors), but swap the ordinary and grey ones for readable ones
            Color fore = fore0 == SystemColors.ControlText || fore0 == SystemColors.WindowText ? text
                       : fore0 == SystemColors.GrayText ? Mix(bg, text, 0.6)
                       : fore0;
            switch (c)
            {
                case Button or ComboBox or NumericUpDown:
                    return;                                     // keep the standard look
                case TextBox tb when !tb.ReadOnly:
                    return;                                     // boxes you type into stay white
                case TextBox or ListBox or ListView or WaveformView:
                    c.BackColor = tint; c.ForeColor = fore;
                    return;
                case DataGridView g:
                    g.BackgroundColor = tint;
                    g.DefaultCellStyle.BackColor = tint;
                    g.DefaultCellStyle.ForeColor = text;
                    g.DefaultCellStyle.SelectionBackColor = SystemColors.Highlight;
                    g.ColumnHeadersDefaultCellStyle.BackColor = Mix(bg, text, 0.15);
                    g.ColumnHeadersDefaultCellStyle.ForeColor = text;
                    g.EnableHeadersVisualStyles = false;
                    return;
                case TabPage tp:
                    tp.UseVisualStyleBackColor = false;
                    break;
            }
            c.BackColor = bg;
            c.ForeColor = fore;
        }
    }
}
