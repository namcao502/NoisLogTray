using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace NoisLogTray;

// The shell's left navigation: one ClickableRow per page, plus Settings at the bottom. The
// rows own focus and Enter/Space activation; this panel only paints its background and lays them out.
internal sealed class SidebarNav : Panel
{
    internal const int ExpandedWidth = 200;
    internal const int CollapsedWidth = 56;
    private const int RowHeight = 38;
    private const int RowGap = 2;
    private const int Inset = 8;
    private const int TopPad = 12;
    private const int RowRadius = 8;
    private const int GlyphColumn = CollapsedWidth - 2 * Inset; // a collapsed row is just the glyph cell

    // Segoe MDL2 Assets draws every icon in the same em box, unlike Segoe UI Symbol glyphs.
    private static readonly Font GlyphFont = UiScale.Font("Segoe MDL2 Assets", 12F);
    private static readonly Font LabelFont = UiScale.Font("Segoe UI", 9.5F);
    private static readonly Font BadgeFont = UiScale.Font("Segoe UI", 7.5F, FontStyle.Bold);
    private static readonly Color BadgeColor = Color.FromArgb(230, 76, 76);

    private readonly List<(ShellPage Page, string Glyph, Func<string> Label, ClickableRow Row)> _items = new();
    private readonly Dictionary<ShellPage, int> _badges = new();
    private readonly ToolTip _tips = new();
    private ClickableRow? _hoverRow;
    private ShellPage? _selected;
    private bool _collapsed;

    internal event Action<ShellPage>? PageSelected;

    internal SidebarNav()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.WindowBg;
        Width = ScaledWidth;

        AddItem(ShellPage.Dashboard, "", () => PageLabel(ShellPage.Dashboard));
        AddItem(ShellPage.Log, "", () => PageLabel(ShellPage.Log));
        AddItem(ShellPage.Tasks, "", () => PageLabel(ShellPage.Tasks));
        AddItem(ShellPage.Week, "", () => PageLabel(ShellPage.Week));
        AddItem(ShellPage.Claude, "", () => PageLabel(ShellPage.Claude));
        AddItem(ShellPage.Settings, "", () => PageLabel(ShellPage.Settings));

        ApplyLanguage();
        Theme.Changed += ApplyTheme;
        Lang.Changed += ApplyLanguage;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= ApplyTheme;
            Lang.Changed -= ApplyLanguage;
            _tips.Dispose();
        }
        base.Dispose(disposing);
    }

    internal static string PageLabel(ShellPage page) => page switch
    {
        ShellPage.Dashboard => "Dashboard",
        ShellPage.Log => "Log",
        ShellPage.Tasks => "Tasks",
        ShellPage.Week => Lang.T("Week", "Tuần"),
        ShellPage.Claude => "Claude",
        ShellPage.Settings => Lang.T("Settings", "Cài đặt"),
        _ => throw new ArgumentOutOfRangeException(nameof(page)),
    };

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal ShellPage Selected
    {
        get => _selected ?? ShellPage.Log;
        set
        {
            _selected = value;
            foreach (var item in _items)
                item.Row.FocusRingColor = item.Page == value ? Color.White : null;
            Invalidate(true);
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Collapsed
    {
        get => _collapsed;
        set
        {
            _collapsed = value;
            Width = ScaledWidth;
            ApplyLanguage(); // tooltips stand in for the hidden labels
            PerformLayout();
            Invalidate(true);
        }
    }

    internal void SetBadge(ShellPage page, int count)
    {
        _badges[page] = Math.Max(0, count);
        ApplyLanguage(); // the accessible name carries the count
        Invalidate(true);
    }

    // Logical pixels through the app zoom, like the rest of the UI.
    private int ScaledWidth => UiScale.S(_collapsed ? CollapsedWidth : ExpandedWidth);

    private int Scaled(int value) => UiScale.S(value);

    private void AddItem(ShellPage page, string glyph, Func<string> label)
    {
        var row = new ClickableRow { BackColor = Theme.WindowBg, Cursor = Cursors.Hand };
        row.Paint += (_, e) => PaintRow(e.Graphics, row, page, glyph, label());
        row.MouseEnter += (_, _) => { _hoverRow = row; row.Invalidate(); };
        row.MouseLeave += (_, _) =>
        {
            if (_hoverRow == row) _hoverRow = null;
            row.Invalidate();
        };
        row.Click += (_, _) => PageSelected?.Invoke(page);
        _items.Add((page, glyph, label, row));
        Controls.Add(row);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var inset = Scaled(Inset);
        var rowHeight = Scaled(RowHeight);
        var rowWidth = Math.Max(1, Width - 2 * inset);
        var y = Scaled(TopPad);
        foreach (var item in _items)
        {
            if (item.Page == ShellPage.Settings)
            {
                item.Row.SetBounds(inset, Math.Max(y, Height - inset - rowHeight), rowWidth, rowHeight);
                continue;
            }
            item.Row.SetBounds(inset, y, rowWidth, rowHeight);
            y += rowHeight + Scaled(RowGap);
        }
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Width = ScaledWidth;
        PerformLayout();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(Theme.WindowBg);
        using var pen = new Pen(Theme.CardBorder, 1f);
        e.Graphics.DrawLine(pen, Width - 1, 0, Width - 1, Height);
        var settingsRow = _items.First(item => item.Page == ShellPage.Settings).Row;
        var dividerY = settingsRow.Top - Scaled(Inset) / 2;
        e.Graphics.DrawLine(pen, 0, dividerY, Width - 2, dividerY);
    }

    // Selected = accent fill with white text, hover = Theme.Hover; collapsed shows the glyph only.
    private void PaintRow(Graphics g, ClickableRow row, ShellPage page, string glyph, string label)
    {
        g.Clear(Theme.WindowBg);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var selected = page == _selected;
        Color? fill = selected ? Theme.Accent : row == _hoverRow ? Theme.Hover : null;
        if (fill is Color fillColor)
        {
            using var path = Rounded(new Rectangle(0, 0, row.Width - 1, row.Height - 1), Scaled(RowRadius));
            using var brush = new SolidBrush(fillColor);
            g.FillPath(brush, path);
        }

        var textColor = selected ? Color.White : Theme.TextPrimary;
        var glyphRect = new Rectangle(0, 0, Math.Min(row.Width, Scaled(GlyphColumn)), row.Height);
        TextRenderer.DrawText(g, glyph, GlyphFont, glyphRect, textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        var badge = _badges.GetValueOrDefault(page);
        using var badgeBrush = new SolidBrush(BadgeColor);
        if (_collapsed)
        {
            if (badge != 0)
            {
                var dot = Scaled(8);
                g.FillEllipse(badgeBrush, glyphRect.Right - Scaled(10), Scaled(6), dot, dot);
            }
            return;
        }

        var badgeSpace = 0;
        if (badge != 0)
        {
            var badgeText = badge > 99 ? "99+" : badge.ToString();
            var badgeHeight = Scaled(16);
            var badgeWidth = Math.Max(badgeHeight, TextRenderer.MeasureText(badgeText, BadgeFont).Width + Scaled(6));
            var badgeRect = new Rectangle(row.Width - Scaled(8) - badgeWidth, (row.Height - badgeHeight) / 2, badgeWidth, badgeHeight);
            using (var path = Rounded(badgeRect, badgeHeight / 2))
                g.FillPath(badgeBrush, path);
            TextRenderer.DrawText(g, badgeText, BadgeFont, badgeRect, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            badgeSpace = badgeWidth + Scaled(8);
        }

        var labelRect = new Rectangle(glyphRect.Right, 0, Math.Max(0, row.Width - glyphRect.Right - badgeSpace - Scaled(8)), row.Height);
        TextRenderer.DrawText(g, label, LabelFont, labelRect, textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private void ApplyTheme()
    {
        BackColor = Theme.WindowBg;
        foreach (var item in _items) item.Row.BackColor = Theme.WindowBg;
        Invalidate(true);
    }

    private void ApplyLanguage()
    {
        foreach (var item in _items)
        {
            var label = item.Label();
            var badge = _badges.GetValueOrDefault(item.Page);
            item.Row.AccessibleName = badge != 0 ? $"{label} ({badge})" : label;
            _tips.SetToolTip(item.Row, _collapsed ? label : null);
        }
        Invalidate(true);
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var d = Math.Max(1, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
