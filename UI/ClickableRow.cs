using System.Drawing;
using System.Drawing.Drawing2D;

namespace NoisLogTray;

// A Panel that takes part in keyboard navigation: it can receive focus, activates on
// Enter/Space (raising Click just like a mouse click), and paints a focus ring. Used for
// clickable list rows (My-tickets suggestions, actionable weekly days) so they are
// reachable without a mouse and announced by a screen reader. Set AccessibleName on each
// instance to the row's content.
internal sealed class ClickableRow : Panel
{
    internal ClickableRow()
    {
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        AccessibleRole = AccessibleRole.Link;
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnMouseDown(MouseEventArgs e) { Focus(); base.OnMouseDown(e); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
        {
            e.Handled = true;
            OnClick(EventArgs.Empty); // fire the same Click handlers a mouse click would
        }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!Focused) return;

        // Dotted accent ring just inside the row edges (children paint over the interior).
        var r = new Rectangle(1, 1, Width - 3, Height - 3);
        using var pen = new Pen(Theme.Accent, 1.4f) { DashStyle = DashStyle.Dot };
        e.Graphics.DrawRectangle(pen, r);
    }
}
