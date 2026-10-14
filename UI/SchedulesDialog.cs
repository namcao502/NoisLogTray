using System.Drawing;

namespace NoisLogTray;

// Settings > "Schedules...": the SchedulesPage in a dialog, shown and hidden with it, so it reads only while open. The
// dialog owns the page and disposes it with itself.
internal sealed class SchedulesDialog : CardDialog
{
    private static readonly Font TitleFont = UiScale.Font("Segoe UI Semibold", 12F, FontStyle.Bold);
    private static readonly int Pad = UiScale.S(24);
    private static readonly int ButtonH = UiScale.S(34);
    private static readonly int BottomInset = UiScale.S(12); // keeps the page off the rounded bottom corners and their border

    internal SchedulesPage Page { get; }
    internal MacButton CloseButton { get; }

    internal SchedulesDialog(SchedulesPage page)
    {
        Page = page;
        Text = Lang.T("Schedules", "Lịch chạy");
        StartPosition = FormStartPosition.CenterParent;
        // The page draws its cards on the window background; the dialog matches it.
        BackColor = Theme.WindowBg;

        var headerHeight = Pad + ButtonH + UiScale.S(4);
        var wanted = UiScale.S(new Size(980, 720));
        var workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        ClientSize = new Size(
            Math.Max(page.MinimumSize.Width + 2, Math.Min(wanted.Width, workingArea.Width - 2 * Pad)),
            Math.Max(page.MinimumSize.Height + headerHeight + BottomInset, Math.Min(wanted.Height, workingArea.Height - 2 * Pad)));

        var title = new Label
        {
            Text = Text,
            AutoSize = true,
            Font = TitleFont,
            ForeColor = Theme.TextPrimary,
            BackColor = Color.Transparent,
        };
        title.Location = new Point(Pad, Pad + (ButtonH - title.PreferredHeight) / 2);
        title.MouseDown += (_, e) => OnMouseDown(e);

        CloseButton = MacButton.Secondary(Lang.T("Close", "Đóng"));
        CloseButton.OnWindow = true;
        CloseButton.DialogResult = DialogResult.Cancel;
        var closeWidth = new[] { "Close", "Đóng" }.Max(label => TextRenderer.MeasureText(label, CloseButton.Font).Width) + UiScale.S(28);
        CloseButton.Size = new Size(Math.Max(UiScale.S(90), closeWidth), ButtonH);
        CloseButton.Location = new Point(ClientSize.Width - Pad - CloseButton.Width, Pad);
        CancelButton = CloseButton;

        page.Bounds = new Rectangle(1, headerHeight, ClientSize.Width - 2, ClientSize.Height - headerHeight - BottomInset);

        Controls.Add(title);
        Controls.Add(CloseButton);
        Controls.Add(page);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Page.OnPageShown();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        Page.OnActivatedRefresh();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Page.OnHidden();
        base.OnFormClosed(e);
    }
}
