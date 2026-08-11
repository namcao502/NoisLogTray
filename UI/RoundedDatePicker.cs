using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace NoisLogTray;

// A rounded, padded, theme-aware date field. Shows the selected date + a chevron;
// clicking opens a ModernCalendar popup. Replaces the native DateTimePicker.
internal sealed class RoundedDatePicker : Control
{
    private DateTime _value = DateTime.Today;

    // True once the user has deliberately chosen a date via the popup. Auto-sync to
    // "today" only happens while this is false, so a manual pick is not clobbered.
    private bool _userPicked;

    internal int Radius = 8;

    internal event EventHandler? ValueChanged;

    internal RoundedDatePicker()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable, true); // Selectable = reachable by Tab / a screen reader
        TabStop = true;
        BackColor = Theme.CardSurface;
        Font = new Font("Segoe UI", 9.5F);
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.DropList;
        UpdateAccessibleName();
        Theme.Changed += OnThemeChanged;
    }

    private void UpdateAccessibleName() => AccessibleName = $"Date, {_value.ToShortDateString()}";

    // Space/Enter opens the calendar, matching a click, so the field is keyboard-operable.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
        {
            e.Handled = true;
            ShowPicker();
        }
        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnMouseDown(MouseEventArgs e) { Focus(); base.OnMouseDown(e); }

    private void OnThemeChanged()
    {
        BackColor = Theme.CardSurface;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal DateTime Value
    {
        get => _value;
        set
        {
            if (_value == value.Date) return;
            _value = value.Date;
            UpdateAccessibleName();
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // Roll the field forward to the system's current date unless the user manually
    // chose one. The tray app can run for days, so the value picked at construction
    // goes stale; call this when the window is (re)opened.
    internal void SyncToTodayIfAuto()
    {
        if (_userPicked) return;
        Value = DateTime.Today;
    }

    // Forget a manual pick so the next window open defaults back to today.
    internal void ForgetManualPick() => _userPicked = false;

    // Set the date programmatically as a deliberate pick (e.g. jumping from the Weekly
    // check to a day that needs logging), so an activation refresh will not roll it back.
    internal void SetDate(DateOnly date)
    {
        _userPicked = true;
        Value = date.ToDateTime(TimeOnly.MinValue);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.CardSurface);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        var d = Radius * 2;
        using var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        using (var brush = new SolidBrush(Theme.InputBg)) g.FillPath(brush, path);
        using (var pen = new Pen(Theme.InputBorder)) g.DrawPath(pen, path);

        var textRect = new Rectangle(10, 0, Width - 34, Height);
        TextRenderer.DrawText(g, _value.ToShortDateString(), Font, textRect, Theme.TextPrimary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        var cx = Width - 18;
        var cy = Height / 2;
        using var chevron = new Pen(Theme.TextSecondary, 1.6f);
        g.DrawLines(chevron, new[] { new Point(cx - 4, cy - 2), new Point(cx, cy + 2), new Point(cx + 4, cy - 2) });

        // Keyboard-focus ring: a dotted accent outline just inside the field border.
        if (Focused)
        {
            var focusRect = Rectangle.Inflate(rect, -2, -2);
            using var path2 = new GraphicsPath();
            var d2 = Math.Max(2, Radius - 1) * 2;
            path2.AddArc(focusRect.X, focusRect.Y, d2, d2, 180, 90);
            path2.AddArc(focusRect.Right - d2, focusRect.Y, d2, d2, 270, 90);
            path2.AddArc(focusRect.Right - d2, focusRect.Bottom - d2, d2, d2, 0, 90);
            path2.AddArc(focusRect.X, focusRect.Bottom - d2, d2, d2, 90, 90);
            path2.CloseFigure();
            using var pen = new Pen(Theme.Accent, 1.4f) { DashStyle = DashStyle.Dot };
            g.DrawPath(pen, path2);
        }
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        ShowPicker();
    }

    private void ShowPicker()
    {
        var calendar = new ModernCalendar { Value = _value, Location = new Point(0, 0) };
        var popup = new CalendarPopupForm();
        popup.ClientSize = calendar.Size;
        popup.Controls.Add(calendar);
        popup.Location = PointToScreen(new Point(0, Height + 4));
        calendar.DateSelected += (_, date) => { _userPicked = true; Value = date; popup.Close(); };
        popup.Deactivate += (_, _) => popup.Close();
        popup.Show();
        popup.Activate();
    }
}
