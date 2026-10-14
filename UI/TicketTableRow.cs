using System.Drawing;
using System.Drawing.Drawing2D;

namespace NoisLogTray;

// One ticket as a table row (the Work board, the Dashboard): key link, summary, then cells drawn at TaskTableLayout's
// columns. The buttons sit in a panel at the right edge, shown while the cursor is over the row or focus is in it.
internal class TicketTableRow : Panel
{
    protected static readonly int ButtonH = UiScale.S(28);
    private static readonly int ButtonGap = UiScale.S(6);
    private static readonly int Pad = UiScale.S(8);
    protected static readonly int RowPadY = UiScale.S(6);
    private static readonly int ColumnGap = UiScale.S(12);
    private static readonly int MinSummaryWidth = UiScale.S(160);
    private static readonly int ActionsLead = UiScale.S(12); // room between the cells the panel covers and its first button
    private static readonly int DotSize = UiScale.S(6);
    private static readonly int DotGap = UiScale.S(5);

    internal const string ActionsName = "actions";

    private const TextFormatFlags CellFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
        | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

    private static readonly Font KeyFont = UiScale.Font("Segoe UI", 9.5F, FontStyle.Bold);
    private static readonly Font SummaryFont = UiScale.Font("Segoe UI", 9.5F);
    private static readonly Font CellFont = UiScale.Font("Segoe UI", 9F);

    private static readonly ThemedToolTip CellTip = new();

    private readonly LinkLabel _keyLink;
    private readonly Panel _actions;
    private readonly TaskTableLayout _layout;
    private IReadOnlyList<TableCell> _cells = Array.Empty<TableCell>();
    private TaskTableColumn? _dotColumn;
    private bool _actionsShown;
    private string? _cellTip;

    internal string TicketKey { get; }

    internal bool ActionsVisible => _actionsShown;

    // The row's background, which its buttons' rounded corners blend into.
    protected Color Surface { get; }

    // columns: the table's columns, Key and Summary among them; the layout keeps only those.
    protected TicketTableRow(string ticketKey, string linkUrl, string summary, IReadOnlyList<TaskTableColumn> columns, int width,
        bool striped)
    {
        TicketKey = ticketKey;
        Margin = new Padding(0);
        DoubleBuffered = true;
        // Every other row is a shade off the card.
        BackColor = striped ? Theme.RowStripe(Theme.CardSurface) : Color.Transparent;
        Surface = striped ? BackColor : Theme.CardSurface;
        var rowHeight = ButtonH + 2 * RowPadY;
        Size = new Size(width, rowHeight);
        _layout = LayoutFor(width, columns);

        var keyColumn = _layout.Columns[TaskTableColumn.Key];
        _keyLink = new LinkLabel
        {
            Text = ticketKey,
            AutoSize = false,
            Font = KeyFont,
            LinkColor = Theme.Accent,
            ActiveLinkColor = Theme.AccentHover,
            LinkBehavior = LinkBehavior.HoverUnderline,
            BackColor = Color.Transparent,
            UseMnemonic = false,
        };
        _keyLink.Bounds = new Rectangle(keyColumn.X, (rowHeight - _keyLink.PreferredHeight) / 2, keyColumn.Width, _keyLink.PreferredHeight);
        _keyLink.LinkClicked += (_, _) => TaskRow.OpenUrl(linkUrl);
        Controls.Add(_keyLink);

        var summaryColumn = _layout.Columns[TaskTableColumn.Summary];
        var summaryLabel = new Label
        {
            Text = summary,
            Bounds = new Rectangle(summaryColumn.X, _keyLink.Top, summaryColumn.Width, _keyLink.PreferredHeight),
            Font = SummaryFont,
            ForeColor = Theme.TextPrimary,
            BackColor = Color.Transparent,
            UseMnemonic = false,
        };
        ThemedToolTip.Track(summaryLabel); // cut with an ellipsis, the full text in a tooltip
        Controls.Add(summaryLabel);

        // Opaque, over the right-hand cells. Its buttons arrive later (SetActions), so they are tracked as they come.
        _actions = new Panel { Name = ActionsName, BackColor = Surface, Visible = false };
        _actions.ControlAdded += (_, e) => TrackPointer(e.Control!);
        Controls.Add(_actions);
        Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Divider });

        CellTip.SetUntilDisposed(this, string.Empty);
        TrackPointer(this);
        Enter += OnPointerOrFocusMoved;
        Leave += OnPointerOrFocusMoved;
    }

    // The table's columns at a row width; a table's header uses it too, so titles and cells line up.
    internal static TaskTableLayout LayoutFor(int rowWidth, IReadOnlyList<TaskTableColumn> columns)
    {
        var fixedWidths = RowColumns.TableWidths(KeyFont, CellFont, TaskTableHeader.TitleFont)
            .Where(kv => columns.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        return TaskTableLayout.For(rowWidth, fixedWidths, ColumnGap, Pad, MinSummaryWidth);
    }

    // dotColumn's cell gets a purple lead dot: the row needs action though its buttons are hidden.
    protected void SetCells(IReadOnlyList<TableCell> cells, TaskTableColumn? dotColumn)
    {
        _cells = cells;
        _dotColumn = dotColumn;
        // The cells are drawn, not controls, so a screen reader hears them on the key link.
        _keyLink.AccessibleDescription = string.Join(", ", cells.Select(tc => $"{TaskTableHeader.Title(tc.Column)} {tc.Text}"));
        Invalidate();
    }

    // Listed right-to-left: the first one sits at the right edge. Added leftmost first, so Tab walks them left to right.
    protected void SetActions(IReadOnlyList<Control> buttonsRightToLeft)
    {
        var actionsWidth = ActionsLead + buttonsRightToLeft.Sum(b => b.Width + ButtonGap) - ButtonGap + Pad;
        _actions.Bounds = new Rectangle(Width - actionsWidth, 0, actionsWidth, Height - 1);
        var buttonX = actionsWidth - Pad;
        foreach (var button in buttonsRightToLeft)
        {
            buttonX -= button.Width;
            button.Location = new Point(buttonX, (Height - ButtonH) / 2);
            buttonX -= ButtonGap;
        }
        for (var buttonIndex = buttonsRightToLeft.Count - 1; buttonIndex >= 0; buttonIndex--)
        {
            _actions.Controls.Add(buttonsRightToLeft[buttonIndex]);
        }
        _actions.BringToFront();
    }

    internal void ShowActions(bool visible)
    {
        if (_actionsShown == visible) return;
        _actionsShown = visible;
        // A row with no buttons has nothing to show.
        _actions.Visible = visible && _actions.Controls.Count != 0;
    }

    internal void RefreshActions(Point cursorInRow, bool hasFocus) => ShowActions(ClientRectangle.Contains(cursorInRow) || hasFocus);

    internal void FocusKey() => _keyLink.Focus();

    // Moving from the row onto one of its controls raises the row's MouseLeave, so every control re-checks the cursor.
    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        if (e.Control != null) TrackPointer(e.Control);
    }

    private void TrackPointer(Control control)
    {
        control.MouseEnter += OnPointerOrFocusMoved;
        control.MouseLeave += OnPointerOrFocusMoved;
    }

    private void OnPointerOrFocusMoved(object? sender, EventArgs e) => RefreshActions(PointToClient(Cursor.Position), ContainsFocus);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        foreach (var cell in _cells)
        {
            if (!_layout.Columns.ContainsKey(cell.Column)) continue;
            var bounds = TextBounds(cell.Column);
            if (cell.Column == _dotColumn)
            {
                using (var dotBrush = new SolidBrush(Theme.ActionReadyToTest))
                {
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    e.Graphics.FillEllipse(dotBrush, bounds.X - DotSize - DotGap, (Height - DotSize) / 2, DotSize, DotSize);
                }
            }
            TextRenderer.DrawText(e.Graphics, cell.Text, CellFont, bounds, cell.Color, CellFlags);
        }
    }

    // A cell's text area: its column, less the lead dot when it has one.
    private Rectangle TextBounds(TaskTableColumn column)
    {
        var (columnX, columnWidth) = _layout.Columns[column];
        var dotWidth = column == _dotColumn ? DotSize + DotGap : 0;
        return new Rectangle(columnX + dotWidth, 0, Math.Max(0, columnWidth - dotWidth), Height - 1);
    }

    // A cut cell's full text, then what the row gave it ("Done in Jira", the PRs in full).
    internal static string TipText(TableCell cell, bool isCut) =>
        string.Join(Environment.NewLine, new[] { isCut ? cell.Text : null, cell.ToolTip }.Where(line => !string.IsNullOrEmpty(line)));

    // The cell under the cursor gets its tooltip.
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var cell = _cells.FirstOrDefault(tc => _layout.Columns.TryGetValue(tc.Column, out var column)
            && e.X >= column.X && e.X < column.X + column.Width);
        var tip = cell == null ? string.Empty : TipText(cell, TextRenderer.MeasureText(cell.Text, CellFont).Width > TextBounds(cell.Column).Width);
        if (tip == _cellTip) return;
        _cellTip = tip;
        CellTip.Hide(this);
        CellTip.SetToolTip(this, tip);
    }
}
