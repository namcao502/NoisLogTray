using System.Drawing;
using System.Drawing.Drawing2D;

namespace NoisLogTray;

// The bell's history as a card under the bell instead of a Windows menu: a title, then the notices newest first
// (NoticeHistoryRow), scrolling past MaxListH. A ToolStripDropDown, so it closes on any click outside (the web views
// included) or Esc, and never takes activation from the window. A press closes it, then runs the notice's target.
internal sealed class NoticeHistoryPopup : ToolStripDropDown
{
    private static readonly Font TitleFont = UiScale.Font("Segoe UI Semibold", 10.5F, FontStyle.Bold);
    private static readonly Font EmptyFont = UiScale.Font("Segoe UI", 9.5F);
    private static readonly int Radius = UiScale.S(10);
    private static readonly int Pad = UiScale.S(6);
    private static readonly int HeaderH = UiScale.S(42);
    private static readonly int EmptyH = UiScale.S(64);
    private static readonly int MaxListH = UiScale.S(440);

    private readonly Panel _card = new();
    private readonly Label _title = new();
    private readonly Panel _list = new() { AutoScroll = true };
    private readonly Label _empty = new();
    private readonly ToolStripControlHost _host;
    private readonly List<NoticeHistoryRow> _rows = new();

    internal IReadOnlyList<NoticeHistoryRow> Rows => _rows;

    internal bool ShowsEmptyState { get; private set; }

    internal NoticeHistoryPopup()
    {
        Padding = Padding.Empty;
        DropShadowEnabled = true;
        Renderer = new CardRenderer();

        _title.AutoSize = false;
        _title.Font = TitleFont;
        _title.TextAlign = ContentAlignment.MiddleLeft;
        _title.BackColor = Color.Transparent;
        _empty.AutoSize = false;
        _empty.Font = EmptyFont;
        _empty.TextAlign = ContentAlignment.MiddleCenter;
        _empty.BackColor = Color.Transparent;
        _card.Paint += PaintCardBorder;
        _card.Controls.Add(_title);
        _card.Controls.Add(_empty);
        _card.Controls.Add(_list);
        ThemedScrollBar.Attach(_list);

        _host = new ToolStripControlHost(_card) { AutoSize = false, Padding = Padding.Empty, Margin = Padding.Empty };
        Items.Add(_host);
    }

    // Rebuilds the card for these notices (oldest first, as kept), `width` wide.
    internal void SetNotices(IReadOnlyList<(DateTime Time, string Message, NoticeKind Kind, Action? Open)> notices, int width)
    {
        foreach (var row in _rows) row.Dispose();
        _rows.Clear();
        _card.BackColor = Theme.CardSurface;
        _list.BackColor = Theme.CardSurface;
        _title.ForeColor = Theme.TextPrimary;
        _empty.ForeColor = Theme.TextSecondary;
        _title.Text = Lang.T("Notifications", "Thông báo");
        _empty.Text = Lang.T("No notifications yet", "Chưa có thông báo");
        _title.SetBounds(UiScale.S(16), 0, width - UiScale.S(32), HeaderH);

        var listWidth = width - 2 * Pad;
        var rowsHeight = BuildRows(notices, listWidth);
        if (rowsHeight > MaxListH)
        {
            // Too long for the card: rows narrow to leave the scrollbar its strip.
            foreach (var row in _rows) row.Dispose();
            _rows.Clear();
            BuildRows(notices, listWidth - SystemInformation.GetVerticalScrollBarWidthForDpi(DeviceDpi));
        }

        ShowsEmptyState = notices.Count == 0;
        _empty.Visible = ShowsEmptyState;
        _list.Visible = !ShowsEmptyState;
        var bodyHeight = notices.Count == 0 ? EmptyH : Math.Min(rowsHeight, MaxListH);
        _empty.SetBounds(Pad, HeaderH, listWidth, EmptyH);
        _list.SetBounds(Pad, HeaderH, listWidth, bodyHeight);
        _list.AutoScrollPosition = Point.Empty;
        _card.Size = new Size(width, HeaderH + bodyHeight + Pad);
        _host.Size = _card.Size;
    }

    private int BuildRows(IReadOnlyList<(DateTime Time, string Message, NoticeKind Kind, Action? Open)> notices, int rowWidth)
    {
        var top = 0;
        for (var noticeIndex = notices.Count - 1; noticeIndex >= 0; noticeIndex--)
        {
            var (time, message, kind, open) = notices[noticeIndex];
            var row = new NoticeHistoryRow(time, message, kind, open != null, rowWidth, striped: _rows.Count % 2 == 1) { Location = new Point(0, top) };
            if (open != null)
            {
                row.Pressed += () =>
                {
                    Close(ToolStripDropDownCloseReason.ItemClicked);
                    open();
                };
            }
            _rows.Add(row);
            _list.Controls.Add(row);
            top += row.Height;
        }
        return top;
    }

    // Rounded corners: the window is cut to the card's shape, and the card paints its border inside it.
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        var oldRegion = Region;
        using (var path = Rounded(new Rectangle(0, 0, Width, Height), Radius))
            Region = new Region(path);
        oldRegion?.Dispose();
    }

    private void PaintCardBorder(object? sender, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Rounded(new Rectangle(0, 0, _card.Width - 1, _card.Height - 1), Radius);
        using var pen = new Pen(Theme.CardBorder, 1f);
        e.Graphics.DrawPath(pen, path);
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

    // The card paints itself; the drop-down adds no menu background or border of its own.
    private sealed class CardRenderer : ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(Theme.CardSurface);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
        }
    }
}
