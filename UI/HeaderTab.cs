using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace NoisLogTray;

// One tab of a HeaderTabStrip: text and × in one pill like a browser tab (a middle click or Delete closes too), and with a
// menu a ▾ between them (or Alt+Down) asks for it.
internal sealed class HeaderTab : Control
{
    private static readonly int PadLeft = UiScale.S(12);
    private static readonly int CloseSize = UiScale.S(18);
    private static readonly int ClosePadRight = UiScale.S(5);
    private static readonly int CloseGap = UiScale.S(6);
    private static readonly int Radius = UiScale.S(6);
    private static readonly int MenuGap = UiScale.S(2);
    private static readonly Font CloseFont = Icons.Regular(13); // inside the 18px hover circle
    private readonly bool _hasMenu;
    private bool _selected;
    private bool _showDot;
    private bool _hover;
    private bool _closeHover;
    private bool _menuHover;

    internal event Action? SelectRequested;
    internal event Action? CloseRequested;
    internal event Action? MenuRequested;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    // Claude waits for the user in this tab.
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool ShowDot
    {
        get => _showDot;
        set { _showDot = value; Invalidate(); }
    }

    internal HeaderTab(string text, int height, bool hasMenu = false)
    {
        _hasMenu = hasMenu;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Text = text;
        Font = UiScale.Font("Segoe UI", 9.5F);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.PageTab;
        var menuWidth = hasMenu ? CloseSize + MenuGap : 0;
        Size = new Size(PadLeft + TextRenderer.MeasureText(text, Font).Width + CloseGap + menuWidth + CloseSize + ClosePadRight, height);
        Theme.Changed += Invalidate;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= Invalidate;
        base.Dispose(disposing);
    }

    internal Rectangle CloseBounds => new(Width - ClosePadRight - CloseSize, (Height - CloseSize) / 2, CloseSize, CloseSize);

    // Left of the ×; empty for a tab without a menu.
    internal Rectangle MenuBounds => _hasMenu
        ? new Rectangle(CloseBounds.Left - MenuGap - CloseSize, (Height - CloseSize) / 2, CloseSize, CloseSize)
        : Rectangle.Empty;

    // The mouse's way in (and the tests'): the × or a middle click closes, the ▾ asks for the menu, a left click anywhere
    // else selects.
    internal void Press(Point location, MouseButtons button)
    {
        if (button == MouseButtons.Middle || button == MouseButtons.Left && CloseBounds.Contains(location)) CloseRequested?.Invoke();
        else if (button == MouseButtons.Left && MenuBounds.Contains(location)) MenuRequested?.Invoke();
        else if (button == MouseButtons.Left) SelectRequested?.Invoke();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (ClientRectangle.Contains(e.Location)) Press(e.Location, e.Button);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var closeHover = CloseBounds.Contains(e.Location);
        var menuHover = MenuBounds.Contains(e.Location);
        if (_hover && closeHover == _closeHover && menuHover == _menuHover) return;
        _hover = true;
        _closeHover = closeHover;
        _menuHover = menuHover;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        _closeHover = false;
        _menuHover = false;
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) SelectRequested?.Invoke();
        else if (e.KeyCode == Keys.Delete) CloseRequested?.Invoke();
        else if (_hasMenu && e.Alt && e.KeyCode == Keys.Down) MenuRequested?.Invoke();
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnChangeUICues(UICuesEventArgs e) { Invalidate(); base.OnChangeUICues(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.WindowBg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        var fill = _selected ? (_hover ? Theme.AccentHover : Theme.Accent) : (_hover ? Theme.SecondaryBtnHover : Theme.SecondaryBtn);
        var foreground = _selected ? Color.White : Theme.SecondaryBtnText;
        using (var path = Rounded(rect, Radius))
        using (var brush = new SolidBrush(fill))
            g.FillPath(brush, path);

        var close = CloseBounds;
        var textRight = _hasMenu ? MenuBounds.Left : close.Left;
        var textRect = new Rectangle(PadLeft, 0, textRight - CloseGap - PadLeft, Height);
        TextRenderer.DrawText(g, Text, Font, textRect, foreground,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        if (_closeHover)
        {
            using var hoverBrush = new SolidBrush(Color.FromArgb(_selected ? 70 : 45, foreground));
            g.FillEllipse(hoverBrush, close);
        }
        Icons.Draw(g, Icons.Dismiss, CloseFont, close, foreground);

        if (_hasMenu)
        {
            if (_menuHover)
            {
                using var menuHoverBrush = new SolidBrush(Color.FromArgb(_selected ? 70 : 45, foreground));
                g.FillEllipse(menuHoverBrush, MenuBounds);
            }
            Icons.Draw(g, Icons.ChevronDown, CloseFont, MenuBounds, foreground);
        }

        if (_showDot)
        {
            var dotDiameter = UiScale.S(8);
            using var dotBrush = new SolidBrush(Theme.Badge);
            g.FillEllipse(dotBrush, rect.Right - dotDiameter - UiScale.S(2), UiScale.S(2), dotDiameter, dotDiameter);
        }

        if (Focused && ShowFocusCues)
        {
            var focusRect = Rectangle.Inflate(rect, -UiScale.S(3), -UiScale.S(3));
            using var focusPath = Rounded(focusRect, Math.Max(UiScale.S(2), Radius - UiScale.S(2)));
            using var pen = new Pen(_selected ? Color.White : Theme.Accent, UiScale.S(1.4f)) { DashStyle = DashStyle.Dot };
            g.DrawPath(pen, focusPath);
        }
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
