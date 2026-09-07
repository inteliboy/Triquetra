// ============================================================
// Theme.cs
// Palette + control factory for the dark/light UI. Theme.IsDark is
// resolved once in Program.Main (AppConfig.Theme override, else the
// real OS setting) and can be changed at runtime via the title-bar
// toggle button; MainForm.ApplyTheme() re-colors everything and
// persists the override via AppConfig.
// ============================================================
using System;
using System.Drawing;
using System.Windows.Forms;

namespace Triquetra
{
    static class Theme
    {
        public static bool IsDark;

        public static Color Accent    { get { return Color.FromArgb(0x00, 0x78, 0xD4); } }
        public static Color AccentHov { get { return Color.FromArgb(0x10, 0x6E, 0xBE); } }
        public static Color AccentFg  { get { return Color.White; } }
        public static Color Bg        { get { return IsDark ? Color.FromArgb(0x1A,0x1A,0x2E) : Color.FromArgb(0xF3,0xF3,0xF8); } }
        public static Color Surface   { get { return IsDark ? Color.FromArgb(0x25,0x25,0x40) : Color.White; } }
        public static Color Border    { get { return IsDark ? Color.FromArgb(0x35,0x35,0x60) : Color.FromArgb(0xD0,0xD0,0xE0); } }
        public static Color Fg        { get { return IsDark ? Color.FromArgb(0xE8,0xE8,0xF0) : Color.FromArgb(0x1A,0x1A,0x2E); } }
        public static Color FgDim     { get { return IsDark ? Color.FromArgb(0x88,0x88,0xAA) : Color.FromArgb(0x60,0x60,0x7A); } }
        public static Color Ok        { get { return IsDark ? Color.FromArgb(0x6C,0xCB,0x6C) : Color.FromArgb(0x21,0x7A,0x21); } }
        public static Color Warn      { get { return IsDark ? Color.FromArgb(0xF0,0xA0,0x50) : Color.FromArgb(0xB8,0x5C,0x00); } }
        public static Color Err       { get { return IsDark ? Color.FromArgb(0xE0,0x50,0x50) : Color.FromArgb(0xC4,0x00,0x00); } }
        public static Color SelBg     { get { return IsDark ? Color.FromArgb(0x3A,0x3A,0x5C) : Color.FromArgb(0xDD,0xE8,0xF5); } }
        public static Color LogBg     { get { return IsDark ? Color.FromArgb(0x12,0x12,0x1E) : Color.FromArgb(0xFA,0xFA,0xFA); } }
        public static Color LogFg     { get { return IsDark ? Color.FromArgb(0xAA,0xAA,0xCC) : Color.FromArgb(0x30,0x30,0x4A); } }
        public static Color EntryBg   { get { return IsDark ? Color.FromArgb(0x2E,0x2E,0x4E) : Color.White; } }
        public static Color EntryFg   { get { return IsDark ? Color.FromArgb(0xE8,0xE8,0xF0) : Color.FromArgb(0x1A,0x1A,0x2E); } }
        public static Color Danger    { get { return Color.FromArgb(0xC4, 0x00, 0x00); } }
    }

    // =========================================================================
    //  Control factory
    // =========================================================================
    static class Ctrl
    {
        public static Font SegoUI(float size, FontStyle style)
        { return new Font("Segoe UI", size, style); }
        public static Font SegoUI(float size) { return SegoUI(size, FontStyle.Regular); }
        public static Font SegoUI()           { return SegoUI(9.75f); }

        public static Button AccentButton(string text)
        {
            Button b = new Button();
            b.Text      = text; b.FlatStyle = FlatStyle.Flat;
            b.Font      = SegoUI(9.75f, FontStyle.Bold);
            b.BackColor = Theme.Accent; b.ForeColor = Theme.AccentFg;
            b.Height    = 34; b.Cursor = Cursors.Hand;
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Theme.AccentHov;
            return b;
        }

        public static Button NormalButton(string text)
        {
            Button b = new Button();
            b.Text      = text; b.FlatStyle = FlatStyle.Flat;
            b.Font      = SegoUI(); b.BackColor = Theme.Surface; b.ForeColor = Theme.Fg;
            b.Height    = 30; b.Cursor = Cursors.Hand;
            b.FlatAppearance.BorderSize         = 1;
            b.FlatAppearance.BorderColor        = Theme.Border;
            b.FlatAppearance.MouseOverBackColor = Theme.SelBg;
            // Tagged so a runtime theme toggle (MainForm.RestyleEntries) can
            // find and re-color it - unlike AccentButton/DangerButton, this
            // button's colors are theme-dependent, not fixed brand colors.
            b.Tag = "theme-normal-btn";
            return b;
        }

        public static Button DangerButton(string text)
        {
            Button b = new Button();
            b.Text      = text; b.FlatStyle = FlatStyle.Flat;
            b.Font      = SegoUI(); b.BackColor = Theme.Danger; b.ForeColor = Color.White;
            b.Height    = 30; b.Cursor = Cursors.Hand;
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(0xA0, 0x00, 0x00);
            return b;
        }

        // Marks a plain Panel/TableLayoutPanel/FlowLayoutPanel so a runtime
        // theme toggle can re-color it too - a bare BackColor assignment at
        // construction time only reflects whatever Theme.IsDark was at
        // startup.
        public static void TagThemeBg(Control c)      { c.Tag = "theme-bg"; }
        public static void TagThemeSurface(Control c) { c.Tag = "theme-surface"; }

        // The height a button needs to show its own font's descenders
        // (p/g/y/j/q) without clipping. Use this instead of guessing a fixed
        // pixel height whenever a button needs to be shorter than the
        // factory default to fit a layout.
        public static int TextButtonHeight(Font font)
        {
            return TextRenderer.MeasureText("Ap gy", font).Height + 10;
        }

        // The height a word-wrapping Label needs to show ALL of its text at
        // a given width without clipping. Use this instead of guessing
        // whenever a Label's Width is fixed and its Height needs to fit
        // however many lines that produces.
        public static int MeasureWrappedHeight(string text, Font font, int width)
        {
            Size sz = TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.Left);
            return sz.Height;
        }

        public static Label MakeLabel(string text, bool dim, float size)
        {
            Label l = new Label();
            l.Text      = text; l.Font = SegoUI(size);
            l.ForeColor = dim ? Theme.FgDim : Theme.Fg;
            l.BackColor = Color.Transparent; l.AutoSize = true;
            return l;
        }
        public static Label MakeLabel(string text, bool dim) { return MakeLabel(text, dim, 9.75f); }
        public static Label MakeLabel(string text)           { return MakeLabel(text, false); }

        public static TextBox MakeEntry(bool password)
        {
            TextBox tb = new TextBox();
            tb.Font = SegoUI(); tb.BackColor = Theme.EntryBg; tb.ForeColor = Theme.EntryFg;
            tb.BorderStyle = BorderStyle.FixedSingle;
            if (password) tb.UseSystemPasswordChar = true;
            return tb;
        }
        public static TextBox MakeEntry() { return MakeEntry(false); }

        public static void ThemeEntry(TextBox tb)
        {
            tb.Font = SegoUI();
            if (Theme.IsDark)
            {
                tb.BackColor = Theme.EntryBg;
                tb.ForeColor = Theme.EntryFg;
            }
            else
            {
                tb.BackColor = SystemColors.Window;
                tb.ForeColor = SystemColors.WindowText;
            }
        }

        // Native CheckBox always paints its 13x13 check GLYPH (not the text)
        // via UxTheme regardless of FlatStyle/BackColor - ThemedCheckBox
        // (ThemedControls.cs) is a fully owner-drawn replacement instead.
        public static CheckBox MakeCheck(string text)
        {
            CheckBox cb = new ThemedCheckBox();
            cb.Text = text; cb.Font = SegoUI();
            // AutoSize (true by default on CheckBox) did not size around
            // ThemedCheckBox's own custom box+text layout - confirmed live
            // (every checkbox label truncated to a few characters plus
            // "..." regardless of a GetPreferredSize override). Size it
            // explicitly here instead, measured the same way the control's
            // own OnPaint lays out box+gap+text.
            cb.AutoSize = false;
            Size sz = TextRenderer.MeasureText(text, cb.Font);
            cb.Width  = 14 + 8 + sz.Width + 4;
            cb.Height = Math.Max(16, sz.Height + 4);
            return cb;
        }

        // Wraps tb/lb in a Panel whose own BackColor draws a themed 1px
        // border - BorderStyle.FixedSingle always renders a fixed native
        // color regardless of theme, and WinForms doesn't expose that
        // border's color as a settable property on TextBox, ListBox, or
        // RichTextBox. The wrapper's Dock/Location/Size are the CALLER's
        // responsibility; this only owns the inner control's BorderStyle
        // and the wrapper's own BackColor/Padding.
        public static Panel Bordered(Control inner)
        {
            TextBox tb = inner as TextBox;
            if (tb != null) tb.BorderStyle = BorderStyle.None;
            ListBox lb = inner as ListBox;
            if (lb != null) lb.BorderStyle = BorderStyle.None;
            RichTextBox rtb = inner as RichTextBox;
            if (rtb != null) rtb.BorderStyle = BorderStyle.None;

            Panel p = new Panel();
            inner.Dock = DockStyle.Fill;
            p.Controls.Add(inner);
            p.BackColor = Theme.Border;
            p.Padding = new Padding(1);
            p.Tag = "theme-border-wrap";
            return p;
        }
    }
}
