using System.Drawing;

namespace NoisLogTray;

// The Work page: the Tasks board with the log column (New entry, Queue, week) on its right, or the column over the
// board when the page is too narrow, which then scrolls. A row's "+ Log" puts its ticket into New entry, and the
// tickets typed there read "In entry" on the board. With Tasks off this session, the board's place says so.
internal sealed class WorkPage : UserControl, IShellPage
{
    private static readonly Font NoticeFont = UiScale.Font("Segoe UI", 9.5F);

    private readonly Panel _body = new();
    private readonly LogPage _logPage;
    private readonly TasksPage? _tasksPage;
    private readonly Label _tasksDisabled = new();

    // The board's search and buttons, for the shell header; none while Tasks is off.
    internal Control? HeaderControl => _tasksPage?.HeaderControl;

    internal WorkPage(LogPage logPage, TasksPage? tasksPage)
    {
        _logPage = logPage;
        _tasksPage = tasksPage;
        BackColor = Theme.WindowBg;
        _body.Dock = DockStyle.Fill;
        _body.BackColor = Theme.WindowBg;

        if (tasksPage != null)
        {
            _body.Controls.Add(tasksPage);
            tasksPage.LogRequested += logPage.AddTicketToInput;
            logPage.EntryTicketsChanged += tasksPage.SetEntryKeys;
        }
        else
        {
            _tasksDisabled.AutoSize = false;
            _tasksDisabled.TextAlign = ContentAlignment.MiddleCenter;
            _tasksDisabled.Font = NoticeFont;
            _tasksDisabled.BackColor = Color.Transparent;
            _body.Controls.Add(_tasksDisabled);
        }
        _body.Controls.Add(logPage);
        Controls.Add(_body);
        ThemedScrollBar.Attach(_body);

        // Stacked, the page scrolls, so only the board's own minimum counts, beside the page's scroll bar.
        MinimumSize = new Size(UiScale.S(TasksPage.DesignWidth) + SystemInformation.VerticalScrollBarWidth, UiScale.S(TasksPage.DesignHeight));
        ApplyLanguage();
        ApplyTheme();
        Theme.Changed += ApplyTheme;
        Lang.Changed += ApplyLanguage;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= ApplyTheme;
            Lang.Changed -= ApplyLanguage;
        }
        base.Dispose(disposing);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        LayoutColumns();
    }

    // ShellLayout works in logical px: convert the page size in, scale the bounds back out. Only Bounds change, so
    // switching between side by side and stacked keeps typed text and the board's scroll.
    private void LayoutColumns()
    {
        var sideBySide = ShellLayout.WorkSideBySide(UiScale.Logical(ClientSize.Width));
        // Only the stacked page scrolls, and its columns stay clear of the scroll bar.
        if (sideBySide && _body.AutoScroll)
        {
            _body.AutoScrollPosition = Point.Empty;
            _body.AutoScroll = false;
        }
        var width = sideBySide ? ClientSize.Width : ClientSize.Width - SystemInformation.GetVerticalScrollBarWidthForDpi(DeviceDpi);

        // Truncated to logical px, so the scaled bounds never pass the edge and bring up a stray scroll bar.
        var (tasks, log) = ShellLayout.WorkBounds((int)(width / UiScale.Factor), (int)(ClientSize.Height / UiScale.Factor),
            LogPage.NaturalHeight, TasksPage.DesignHeight);
        var scroll = _body.AutoScrollPosition;
        _body.SuspendLayout();
        _logPage.SetStacked(!sideBySide);
        _logPage.Bounds = Scrolled(UiScale.S(log), scroll);
        Control tasksArea = _tasksPage != null ? _tasksPage : _tasksDisabled;
        tasksArea.Bounds = Scrolled(UiScale.S(tasks), scroll);
        _body.ResumeLayout();
        // Turned on only once the columns fit: the side-by-side bounds would overflow and scroll the page sideways.
        if (!sideBySide && !_body.AutoScroll) _body.AutoScroll = true;
    }

    // Child bounds are relative to the scrolled view, so a column is placed at its layout bounds plus the scroll.
    private static Rectangle Scrolled(Rectangle bounds, Point scroll) =>
        new(bounds.X + scroll.X, bounds.Y + scroll.Y, bounds.Width, bounds.Height);

    private void ApplyLanguage() =>
        _tasksDisabled.Text = Lang.T(TrayApp.TasksDisabledEnglish, TrayApp.TasksDisabledVietnamese);

    private void ApplyTheme()
    {
        BackColor = Theme.WindowBg;
        _body.BackColor = Theme.WindowBg;
        _tasksDisabled.ForeColor = Theme.TextSecondary;
    }

    public void OnPageShown()
    {
        _body.AutoScrollPosition = Point.Empty; // stacked, New entry at the top is where focus lands
        _logPage.OnPageShown();
        _tasksPage?.OnPageShown();
    }

    public void OnActivatedRefresh()
    {
        _logPage.OnActivatedRefresh();
        _tasksPage?.OnActivatedRefresh();
    }

    public void OnHidden()
    {
        _logPage.OnHidden();
        _tasksPage?.OnHidden();
    }
}
