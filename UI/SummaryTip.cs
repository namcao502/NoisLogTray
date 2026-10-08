using System.Windows.Forms;

namespace NoisLogTray;

// Hover tooltip with the full Jira summary for a "Will log" row whose summary was cut
// with "..." (or not drawn at all for lack of room). Rows call Update from OnPaint, so
// the tooltip follows resizes; the last text is cached to avoid re-registering per paint.
internal sealed class SummaryTip : IDisposable
{
    private readonly ToolTip _tip = new();
    private string? _shownText;

    internal void Update(Control row, string summary, bool isCut)
    {
        var text = isCut ? summary : null;
        if (text == _shownText) return;
        _shownText = text;
        _tip.SetToolTip(row, text);
    }

    public void Dispose() => _tip.Dispose();
}
