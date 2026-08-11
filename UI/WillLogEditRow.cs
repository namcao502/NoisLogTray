using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace NoisLogTray;

// One editable "Will log" line: status dot, colored ticket key, an inline hours field,
// and the computed time slots (right-aligned). Owner-drawn like WillLogRow, but hosts a
// small borderless TextBox for the hours. Commits on blur/Enter (not per keystroke) so
// the parent can re-flow all rows without stealing focus mid-edit. HoursChanged carries
// the row index and the parsed hours.
internal sealed class WillLogEditRow : Control
{
    private static readonly Font KeyFont = new("Segoe UI", 9F, FontStyle.Bold);
    private static readonly Font TextFont = new("Segoe UI", 9F);

    private const int SlotsW = 120;
    private const int HoursW = 40;
    private const int UnitW = 12; // room for the trailing "h"

    private const TextFormatFlags LeftFlags =
        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
    private const TextFormatFlags RightFlags =
        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;

    private readonly TextBox _hours = new();

    // True once the user has typed into the field. A no-op focus in/out must not commit,
    // because the 1-decimal display is lossy (e.g. 160 min shows "2.7" -> 162 min) and
    // would drift the total / falsely trip the over-8h guard.
    private bool _dirty;

    internal Color DotColor = Color.FromArgb(150, 150, 156);
    internal Color KeyColor = Color.Gray;
    internal string Slots = "";
    internal int Index;

    // Raised when the user commits a new hours value (parsed, >= 0).
    internal event Action<int, double>? HoursChanged;

    private string _key = "";

    // The ticket key. Setting it also names the hours field for screen readers.
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string Key
    {
        get => _key;
        set
        {
            _key = value;
            _hours.AccessibleName = $"Hours for {value}";
            AccessibleName = value;
        }
    }

    internal WillLogEditRow()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 26;
        Margin = new Padding(0);

        _hours.BorderStyle = BorderStyle.None;
        _hours.Font = TextFont;
        _hours.TextAlign = HorizontalAlignment.Center;
        _hours.BackColor = Theme.InputBg;
        _hours.ForeColor = Theme.TextPrimary;
        _hours.TextChanged += (_, _) => _dirty = true;
        _hours.Leave += (_, _) => Commit();
        _hours.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            // Commit re-flows every row, disposing this one; defer so we are not tearing
            // down the focused textbox from inside its own KeyDown.
            BeginInvoke(new Action(Commit));
        };
        Controls.Add(_hours);
    }

    // Current hours, shown with up to one decimal (e.g. 4, 2.7).
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal double Hours
    {
        get => double.TryParse(_hours.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) ? h : 0;
        // Programmatic set is not a user edit; the TextChanged above will flip _dirty, so
        // clear it afterwards to keep a subsequent no-op blur from committing.
        set { _hours.Text = value.ToString("0.#", CultureInfo.InvariantCulture); _dirty = false; }
    }

    private void Commit()
    {
        if (!_dirty) return;
        if (double.TryParse(_hours.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) && h >= 0)
            HoursChanged?.Invoke(Index, h);
    }

    // The hours field sits just left of the right-aligned slots text.
    private Rectangle HoursRect()
    {
        const int boxH = 18;
        var boxY = (Height - boxH) / 2;
        var slotsX = Width - SlotsW - 2;
        var boxRight = slotsX - UnitW - 4;
        return new Rectangle(boxRight - HoursW, boxY, HoursW, boxH);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        var r = HoursRect();
        var h = _hours.PreferredHeight;
        _hours.Bounds = new Rectangle(r.X + 3, r.Y + Math.Max(0, (r.Height - h) / 2), r.Width - 6, h);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.InputBg);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var dot = new SolidBrush(DotColor))
            g.FillEllipse(dot, 4, (Height - 8) / 2f, 8, 8);

        var box = HoursRect();
        var keyRect = new Rectangle(18, 0, box.Left - 26, Height);
        TextRenderer.DrawText(g, Key, KeyFont, keyRect, KeyColor, LeftFlags);

        // Field border + trailing "h" unit.
        using (var path = Rounded(box, 4))
        using (var pen = new Pen(Theme.Divider))
            g.DrawPath(pen, path);
        var unitRect = new Rectangle(box.Right + 1, 0, UnitW, Height);
        TextRenderer.DrawText(g, "h", TextFont, unitRect, Theme.TextSecondary, LeftFlags);

        var slotsRect = new Rectangle(Width - SlotsW - 2, 0, SlotsW, Height);
        TextRenderer.DrawText(g, Slots, TextFont, slotsRect, Theme.TextSecondary, RightFlags);
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
