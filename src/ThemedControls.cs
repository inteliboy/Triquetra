// ============================================================
// ThemedControls.cs
// Fully owner-drawn replacement for CheckBox, ported from
// LenovoRepoBuilder\src\ThemedControls.cs. CheckBox's 13x13 check
// GLYPH (not the text) always paints via UxTheme regardless of
// FlatStyle/BackColor - confirmed live (screenshot + pixel sample) in
// that project that neither FlatStyle.Flat nor a dark scrollbar theme
// hint changes it at all. Reads Theme.* live in OnPaint, so a runtime
// theme toggle needs only Invalidate(), not per-instance recoloring.
// ============================================================
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Triquetra
{
    class ThemedCheckBox : CheckBox
    {
        const int BoxSize = 14;

        public ThemedCheckBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                      ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Parent.BackColor alone isn't enough here - an intermediate
            // layout panel (FlowLayoutPanel/TableLayoutPanel) is commonly
            // left Color.Transparent so IT shows through to whatever's
            // behind it, but filling this control's own background with a
            // literal Color.Transparent brush paints nothing at all,
            // leaving stale/overlapping content from neighboring controls
            // visible underneath - confirmed live (checkbox rows on the
            // Settings tab rendered as garbled overlapping text) when the
            // immediate Parent (a FlowLayoutPanel) was transparent. Walk up
            // to the nearest ancestor with a real opaque color instead.
            Color parentBg = Theme.Bg;
            for (Control p = Parent; p != null; p = p.Parent)
            {
                if (p.BackColor != Color.Transparent) { parentBg = p.BackColor; break; }
            }
            using (SolidBrush pb = new SolidBrush(parentBg)) g.FillRectangle(pb, ClientRectangle);

            int boxTop = (Height - BoxSize) / 2;
            Rectangle box = new Rectangle(1, boxTop, BoxSize, BoxSize);

            Color fill = Checked ? Theme.Accent : Theme.EntryBg;
            Color border = Checked ? Theme.Accent : Theme.Border;
            using (SolidBrush b = new SolidBrush(fill)) g.FillRectangle(b, box);
            using (Pen p = new Pen(border)) g.DrawRectangle(p, box.X, box.Y, box.Width - 1, box.Height - 1);

            if (Checked)
            {
                using (Pen check = new Pen(Theme.AccentFg, 2f))
                {
                    check.StartCap = LineCap.Round;
                    check.EndCap = LineCap.Round;
                    g.DrawLine(check, box.X + 3, box.Y + 7, box.X + 6, box.Y + 10);
                    g.DrawLine(check, box.X + 6, box.Y + 10, box.X + 11, box.Y + 3);
                }
            }

            Rectangle textRect = new Rectangle(BoxSize + 8, 0, System.Math.Max(0, Width - BoxSize - 8), Height);
            Color fg = Enabled ? Theme.Fg : Theme.FgDim;
            TextRenderer.DrawText(g, Text, Font, textRect, fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            if (Focused)
            {
                Rectangle focusRect = new Rectangle(BoxSize + 6, 2, System.Math.Max(0, Width - BoxSize - 10), Height - 4);
                ControlPaint.DrawFocusRectangle(g, focusRect);
            }
        }

        protected override void OnGotFocus(System.EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(System.EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnCheckedChanged(System.EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }

        // CheckBox.AutoSize (true by default) calls this to size itself -
        // the base implementation measures for the NATIVE checkbox glyph,
        // not this control's own BoxSize+gap layout, so AutoSize silently
        // undersized every real checkbox label to a few characters plus
        // "..." (confirmed live: "Failsafe mode ..." truncated to
        // "Failsafe m..."). Override with the real measurement this
        // control's own OnPaint actually uses.
        public override Size GetPreferredSize(Size proposedSize)
        {
            Size textSize = TextRenderer.MeasureText(Text, Font);
            int w = BoxSize + 8 + textSize.Width + 4;
            int h = System.Math.Max(BoxSize + 2, textSize.Height + 4);
            return new Size(w, h);
        }
    }
}
