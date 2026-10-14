using System.Drawing;

namespace NoisLogTray;

// The column titles over the Work board's rows, drawn at the same TaskTableLayout as TaskRow so they line up.
internal sealed class TaskTableHeader : Control
{
    internal static readonly Font TitleFont = UiScale.Font("Segoe UI", 8F, FontStyle.Bold);

    private const TextFormatFlags TitleFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
        | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

    private TaskTableLayout? _layout;

    internal TaskTableHeader()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.Selectable, false);
        TabStop = false;
    }

    internal static string Title(TaskTableColumn column)
    {
        var (english, vietnamese) = TitleForms(column);
        return Lang.T(english, vietnamese);
    }

    // Both languages, so RowColumns sizes a column for the wider one and a language switch never moves the table.
    internal static (string English, string Vietnamese) TitleForms(TaskTableColumn column) => column switch
    {
        TaskTableColumn.Key => ("TICKET", "TICKET"),
        TaskTableColumn.Summary => ("", ""),
        TaskTableColumn.Status => ("STATUS", "TRẠNG THÁI"),
        TaskTableColumn.Due => ("DUE", "HẠN"),
        TaskTableColumn.Eta => ("ETA", "ETA"),
        TaskTableColumn.Priority => ("P", "P"),
        TaskTableColumn.Logged => ("LOGGED", "ĐÃ LOG"),
        TaskTableColumn.PrMain => ("MAIN", "MAIN"),
        TaskTableColumn.PrDev => ("DEV", "DEV"),
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, null),
    };

    internal void SetLayout(TaskTableLayout layout)
    {
        _layout = layout;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using (var fill = new SolidBrush(Theme.CardSurface))
            e.Graphics.FillRectangle(fill, ClientRectangle);
        using (var divider = new Pen(Theme.Divider))
            e.Graphics.DrawLine(divider, 0, Height - 1, Width, Height - 1);
        if (_layout == null) return;
        foreach (var (column, cellColumn) in _layout.Columns)
        {
            TextRenderer.DrawText(e.Graphics, Title(column), TitleFont, new Rectangle(cellColumn.X, 0, cellColumn.Width, Height - 1),
                Theme.TextSecondary, TitleFlags);
        }
    }
}
