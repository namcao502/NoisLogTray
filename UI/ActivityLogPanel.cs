using System.Drawing;
using System.Globalization;

namespace NoisLogTray;

// The bottom "activity" block: a Card holding a scrolling console log (timestamped,
// with green/red result lines) and, below it, the TSC/HRM progress bars shown only
// during a log run. Docks itself to the bottom of the window and self-themes. Append
// and the progress reports are safe to call from a background thread.
internal sealed class ActivityLogPanel : Panel
{
    private const int CardW = 560;
    private const int InnerW = 528; // CardW - 2*16
    private const int CardH = 150;
    private const int LogTop = 38;
    private const int ProgHrmY = CardH - 28; // 122
    private const int ProgTscY = ProgHrmY - 22; // 100
    private const int Cap = 200; // keep the last N lines; trim older ones

    private static readonly Color OkColor = Color.FromArgb(46, 160, 80);
    private static readonly Color ErrColor = Color.FromArgb(230, 76, 76);
    private static readonly Font SectionFont = new("Segoe UI", 8.5F, FontStyle.Bold);

    private readonly Card _card = new();
    private readonly Label _title = new();
    private readonly RoundedHost _host = new();
    private readonly RichTextBox _log = new();
    private readonly MiniProgress _tscBar = new();
    private readonly MiniProgress _hrmBar = new();
    private readonly Label _tscLabel = new();
    private readonly Label _hrmLabel = new();
    private readonly Label _tscPct = new();
    private readonly Label _hrmPct = new();
    private readonly List<Line> _lines = new();

    // One console line; time is captured on add so a theme re-render keeps the stamp.
    private readonly record struct Line(DateTime Time, string Text, bool IsResult, bool Ok);

    internal ActivityLogPanel()
    {
        Dock = DockStyle.Bottom;
        Height = CardH + 16; // 8px margin above and below the card
        BackColor = Theme.WindowBg;

        _card.Size = new Size(CardW, CardH);
        _card.Location = new Point(20, 8);

        _title.Text = "ACTIVITY";
        _title.AutoSize = true;
        _title.Location = new Point(16, 14);
        _title.Font = SectionFont;
        _title.ForeColor = Theme.TextSecondary;
        _title.BackColor = Color.Transparent;
        _card.Controls.Add(_title);

        _host.Location = new Point(16, LogTop);
        _log.Dock = DockStyle.Fill;
        _log.BorderStyle = BorderStyle.None;
        _log.ReadOnly = true;
        _log.TabStop = false;
        _log.WordWrap = true;
        _log.ScrollBars = RichTextBoxScrollBars.Vertical;
        _log.BackColor = Theme.InputBg;
        _log.ForeColor = Theme.TextPrimary;
        _log.Font = new Font("Consolas", 9F);
        _log.Cursor = Cursors.Default;
        _host.Controls.Add(_log);
        _card.Controls.Add(_host);

        ConfigureProgressRow(_tscLabel, "TSC", _tscBar, _tscPct, ProgTscY);
        ConfigureProgressRow(_hrmLabel, "HRM", _hrmBar, _hrmPct, ProgHrmY);

        Controls.Add(_card);
        LayoutLog(progressVisible: false);

        Theme.Changed += ApplyTheme;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= ApplyTheme;
        base.Dispose(disposing);
    }

    // Append a line (normal, or a green/red result). Safe from any thread.
    internal void Append(string text, bool isResult, bool ok)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string, bool, bool>(Append), text, isResult, ok);
            return;
        }

        _lines.Add(new Line(DateTime.Now, text, isResult, ok));
        if (_lines.Count > Cap)
        {
            _lines.RemoveRange(0, _lines.Count - Cap);
            Render(); // list trimmed; rebuild so the box matches
            return;
        }
        AppendLineToBox(_lines[^1]);
    }

    internal void ShowProgress(bool tsc, bool hrm)
    {
        _tscBar.SetFraction(0);
        _hrmBar.SetFraction(0);
        _tscPct.Text = "0%";
        _hrmPct.Text = "0%";
        LayoutLog(progressVisible: true);
        _tscLabel.Visible = _tscBar.Visible = _tscPct.Visible = tsc;
        _hrmLabel.Visible = _hrmBar.Visible = _hrmPct.Visible = hrm;
    }

    internal void HideProgress() => LayoutLog(progressVisible: false);

    internal void ReportTsc(int done, int total) => Report(_tscBar, _tscPct, done, total);
    internal void ReportHrm(int done, int total) => Report(_hrmBar, _hrmPct, done, total);

    private void Report(MiniProgress bar, Label pct, int done, int total)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => Report(bar, pct, done, total)));
            return;
        }
        var fraction = total > 0 ? (double)done / total : 0;
        bar.SetFraction(fraction);
        pct.Text = $"{(int)Math.Round(fraction * 100)}%";
    }

    private void ApplyTheme()
    {
        BackColor = Theme.WindowBg;
        _title.ForeColor = Theme.TextSecondary;
        _log.BackColor = Theme.InputBg;
        _log.ForeColor = Theme.TextPrimary;
        _tscLabel.ForeColor = _hrmLabel.ForeColor = Theme.TextSecondary;
        _tscPct.ForeColor = _hrmPct.ForeColor = Theme.TextSecondary;
        Render();
    }

    // Rebuild the whole console from the backing list, applying the current theme.
    private void Render()
    {
        _log.Clear();
        foreach (var line in _lines) AppendLineToBox(line);
    }

    private void AppendLineToBox(Line line)
    {
        var color = line.IsResult ? (line.Ok ? OkColor : ErrColor) : Theme.TextPrimary;
        _log.SelectionStart = _log.TextLength;
        _log.SelectionLength = 0;

        // Dim timestamp prefix, then the message in its own colour.
        _log.SelectionColor = Theme.TextSecondary;
        _log.AppendText(line.Time.ToString("yyyy-MM-dd h:mm:ss tt", CultureInfo.InvariantCulture) + "  ");
        _log.SelectionColor = color;
        _log.AppendText(line.Text + Environment.NewLine);

        _log.SelectionColor = _log.ForeColor;
        _log.SelectionStart = _log.TextLength;
        if (_log.IsHandleCreated) _log.ScrollToCaret();
    }

    private void ConfigureProgressRow(Label label, string text, MiniProgress bar, Label pct, int y)
    {
        label.Text = text;
        label.AutoSize = false;
        label.Bounds = new Rectangle(16, y - 2, 36, 16);
        label.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold);
        label.ForeColor = Theme.TextSecondary;
        label.BackColor = Color.Transparent;
        label.TextAlign = ContentAlignment.MiddleLeft;

        bar.Bounds = new Rectangle(54, y + 3, 440, 6);

        pct.Text = "0%";
        pct.AutoSize = false;
        pct.Bounds = new Rectangle(500, y - 2, 44, 16);
        pct.Font = new Font("Segoe UI", 8.5F);
        pct.ForeColor = Theme.TextSecondary;
        pct.BackColor = Color.Transparent;
        pct.TextAlign = ContentAlignment.MiddleRight;

        _card.Controls.Add(label);
        _card.Controls.Add(bar);
        _card.Controls.Add(pct);
    }

    // Size the console to leave room for the progress bars only while they show.
    private void LayoutLog(bool progressVisible)
    {
        _tscLabel.Visible = _tscBar.Visible = _tscPct.Visible = progressVisible;
        _hrmLabel.Visible = _hrmBar.Visible = _hrmPct.Visible = progressVisible;
        var bottom = progressVisible ? ProgTscY - 8 : CardH - 12;
        _host.Size = new Size(InnerW, Math.Max(40, bottom - _host.Top));
    }
}
