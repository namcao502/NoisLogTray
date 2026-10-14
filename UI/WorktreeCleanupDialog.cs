using System.Drawing;
using System.Globalization;

namespace NoisLogTray;

// Settings > "Clean up worktrees...": a table, one row per ticket with a verdict (Ready / Check / Keep / no worktree) and
// the action that fits it. Every action re-reads only its own row afterwards, and none runs while another does.
internal sealed class WorktreeCleanupDialog : CardDialog
{
    private static readonly Font TitleFont = UiScale.Font("Segoe UI Semibold", 12F, FontStyle.Bold);
    private static readonly Font KeyFont = UiScale.Font("Segoe UI Semibold", 10F, FontStyle.Bold);
    private static readonly Font TextFont = UiScale.Font("Segoe UI", 9F);
    private static readonly Font ButtonFont = UiScale.Font("Segoe UI", 9.5F); // MacButton's
    private static readonly int Pad = UiScale.S(24);
    private static readonly int DialogW = UiScale.S(900);
    private static readonly int DialogH = UiScale.S(640);
    private static readonly int ButtonH = UiScale.S(34);
    private static readonly int ButtonGap = UiScale.S(10);
    private static readonly int RowH = UiScale.S(44);
    private static readonly int RowPad = UiScale.S(12);
    private static readonly int ColumnGap = UiScale.S(12);
    private static readonly int RowButtonH = UiScale.S(28);
    private static readonly int ColumnTitlesH = TaskTableHeader.TitleFont.Height + UiScale.S(10);
    private const double BytesPerMegabyte = 1024 * 1024;

    private readonly Func<Task<List<WorktreeCleanupRow>>> _loadRows;
    private readonly Func<string, Task<WorktreeCleanupRow>> _loadRow;
    private readonly Func<WorktreeCleanupRow, Task<string?>> _remove;
    private readonly Func<string, Task<string?>> _slimDocs;
    private readonly Func<string, Task<string?>> _mineKnowledge;
    private readonly Label _summary;
    private readonly Label _loading;
    private readonly Panel _list;
    private readonly Panel _columnTitles;
    private readonly Dictionary<string, string> _errors = new();
    private readonly Dictionary<string, MacButton> _removeButtons = new();
    private readonly Dictionary<string, MacButton> _slimButtons = new();
    private readonly Dictionary<string, MacButton> _mineButtons = new();
    private List<WorktreeCleanupRow> _rows = new();
    private bool _isBusy;
    private bool _closed;

    internal MacButton RemoveAllButton { get; }
    internal MacButton SlimAllButton { get; }
    internal MacButton CloseButton { get; }

    internal WorktreeCleanupDialog(Func<Task<List<WorktreeCleanupRow>>> loadRows, Func<string, Task<WorktreeCleanupRow>> loadRow,
        Func<WorktreeCleanupRow, Task<string?>> remove, Func<string, Task<string?>> slimDocs, Func<string, Task<string?>> mineKnowledge)
    {
        _loadRows = loadRows;
        _loadRow = loadRow;
        _remove = remove;
        _slimDocs = slimDocs;
        _mineKnowledge = mineKnowledge;
        Text = Lang.T("Clean up worktrees", "Dọn worktree");
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        ClientSize = new Size(DialogW, DialogH);

        var title = new Label
        {
            Text = Text,
            AutoSize = true,
            Font = TitleFont,
            ForeColor = Theme.TextPrimary,
            BackColor = Color.Transparent,
        };
        // Centered on the header buttons' row; the summary goes under that row, so it never covers a button.
        title.Location = new Point(Pad, Pad + (ButtonH - title.PreferredHeight) / 2);
        title.MouseDown += (_, e) => OnMouseDown(e);

        _summary = new Label
        {
            AutoSize = false,
            Font = TextFont,
            ForeColor = Theme.TextSecondary,
            BackColor = Color.Transparent,
            Location = new Point(Pad, Pad + ButtonH + UiScale.S(8)),
            Size = new Size(DialogW - 2 * Pad, UiScale.S(20)),
        };
        _summary.MouseDown += (_, e) => OnMouseDown(e);

        // Right-aligned like ConfirmDialog, the destructive action first from the left.
        CloseButton = MacButton.Secondary(Lang.T("Close", "Đóng"));
        CloseButton.DialogResult = DialogResult.Cancel;
        RemoveAllButton = MacButton.Tinted("", Theme.Badge);
        SlimAllButton = MacButton.Secondary("");
        // Each wide enough for its longer language and a three-digit count, so no label clips.
        var headerButtons = new (MacButton Button, string[] Labels)[]
        {
            (CloseButton, ["Close", "Đóng"]),
            (SlimAllButton, ["Trim all mined (999)", "Thu gọn mọi ticket đã mine (999)"]),
            (RemoveAllButton, ["Remove all ready (999)", "Xóa mọi worktree sẵn sàng (999)"]),
        };
        var buttonsRight = DialogW - Pad;
        foreach (var (button, labels) in headerButtons)
        {
            var labelW = labels.Max(l => TextRenderer.MeasureText(l, button.Font).Width);
            button.Size = new Size(Math.Max(UiScale.S(120), labelW + UiScale.S(28)), ButtonH);
            button.Location = new Point(buttonsRight - button.Width, Pad);
            buttonsRight -= button.Width + ButtonGap;
        }
        RemoveAllButton.Click += async (_, _) => await RemoveAllReadyAsync();
        SlimAllButton.Click += async (_, _) => await SlimAllMinedAsync();
        CancelButton = CloseButton;

        var titlesTop = _summary.Bottom + UiScale.S(14);
        _columnTitles = new Panel
        {
            BackColor = Color.Transparent,
            Location = new Point(Pad, titlesTop),
            Size = new Size(DialogW - 2 * Pad, ColumnTitlesH),
        };
        _columnTitles.Paint += (_, e) => PaintColumnTitles(e.Graphics);
        _columnTitles.MouseDown += (_, e) => OnMouseDown(e);
        var listTop = _columnTitles.Bottom;
        _list = new Panel
        {
            AutoScroll = true,
            BackColor = Color.Transparent,
            Location = new Point(Pad, listTop),
            Size = new Size(DialogW - 2 * Pad, DialogH - listTop - Pad),
        };
        _loading = new Label
        {
            Text = Lang.T("Reading worktrees...", "Đang đọc worktree..."),
            AutoSize = true,
            Font = TextFont,
            ForeColor = Theme.TextSecondary,
            BackColor = Color.Transparent,
            Location = new Point(RowPad, RowPad),
        };
        _list.Controls.Add(_loading);

        Controls.Add(title);
        Controls.Add(_summary);
        Controls.Add(RemoveAllButton);
        Controls.Add(SlimAllButton);
        Controls.Add(CloseButton);
        Controls.Add(_columnTitles);
        Controls.Add(_list);
        ThemedScrollBar.Attach(_list);
        UpdateHeader();
    }

    // Null when the row has no such button.
    internal MacButton? RemoveButtonFor(string ticketKey)
    {
        return _removeButtons.GetValueOrDefault(ticketKey);
    }

    internal MacButton? SlimButtonFor(string ticketKey)
    {
        return _slimButtons.GetValueOrDefault(ticketKey);
    }

    internal MacButton? MineButtonFor(string ticketKey)
    {
        return _mineButtons.GetValueOrDefault(ticketKey);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        SetBusy(true);
        List<WorktreeCleanupRow> rows;
        try
        {
            rows = await _loadRows();
        }
        catch (Exception ex)
        {
            // An async void handler: an escaping exception would take the app down, and the dialog would sit on "Reading...".
            AppLogger.Error($"[cleanup] reading the worktrees failed: {ex}");
            if (!IsDisposed)
            {
                _loading.Text = Lang.T("Could not read the worktrees (see log).", "Không đọc được worktree (xem log).");
            }

            return;
        }

        if (IsDisposed)
        {
            return;
        }

        SetBusy(false);
        ShowRows(rows);
    }

    // The first load ends here; xUnit has no message loop, so the tests call it directly.
    internal void ShowRows(IReadOnlyList<WorktreeCleanupRow> rows)
    {
        _rows = rows.ToList();
        RebuildList();
    }

    private IReadOnlyList<WorktreeCleanupRow> ReadyRows()
    {
        return _rows.Where(wcr => wcr.Verdict == CleanupVerdict.Ready).ToList();
    }

    private IReadOnlyList<WorktreeCleanupRow> MinedSlimRows()
    {
        return _rows.Where(wcr => wcr.Verdict == CleanupVerdict.NoWorktree && wcr.IsMined && wcr.SlimBytes > 0).ToList();
    }

    private void UpdateHeader()
    {
        var readyRowCount = ReadyRows().Count;
        var minedSlimRowCount = MinedSlimRows().Count;
        RemoveAllButton.Text = Lang.T($"Remove all ready ({readyRowCount})", $"Xóa mọi worktree sẵn sàng ({readyRowCount})");
        SlimAllButton.Text = Lang.T($"Trim all mined ({minedSlimRowCount})", $"Thu gọn mọi ticket đã mine ({minedSlimRowCount})");
        RemoveAllButton.Enabled = !_isBusy && readyRowCount != 0;
        SlimAllButton.Enabled = !_isBusy && minedSlimRowCount != 0;
        if (_rows.Count == 0)
        {
            _summary.Text = _loading.Parent == null ? Lang.T("Nothing to clean up.", "Không có gì để dọn.") : "";
            return;
        }

        _summary.Text = Lang.T(
            $"{readyRowCount} ready, {_rows.Count(wcr => wcr.Verdict == CleanupVerdict.Check)} to check, {_rows.Count(wcr => wcr.Verdict == CleanupVerdict.Keep)} to keep, {_rows.Count(wcr => wcr.Verdict == CleanupVerdict.NoWorktree)} without a worktree",
            $"{readyRowCount} sẵn sàng, {_rows.Count(wcr => wcr.Verdict == CleanupVerdict.Check)} cần kiểm tra, {_rows.Count(wcr => wcr.Verdict == CleanupVerdict.Keep)} giữ lại, {_rows.Count(wcr => wcr.Verdict == CleanupVerdict.NoWorktree)} không có worktree");
    }

    // Keeps the scroll position, so a finished action does not throw the user back to the top.
    private void RebuildList()
    {
        var scrollTop = -_list.AutoScrollPosition.Y;
        _list.SuspendLayout();
        foreach (var control in _list.Controls.Cast<Control>().ToList())
        {
            _list.Controls.Remove(control);
            control.Dispose();
        }

        _removeButtons.Clear();
        _slimButtons.Clear();
        _mineButtons.Clear();
        var rowWidth = _list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth;
        for (var rowIndex = 0; rowIndex < _rows.Count; rowIndex++)
        {
            var row = BuildRow(_rows[rowIndex], rowIndex % 2 == 1, rowWidth);
            row.Location = new Point(0, rowIndex * RowH);
            _list.Controls.Add(row);
        }

        _list.AutoScrollMinSize = new Size(0, _rows.Count * RowH);
        _list.AutoScrollPosition = new Point(0, scrollTop);
        _list.ResumeLayout();
        UpdateHeader();
    }

    // Over the list, at the rows' column x; titles stay English-only where the two languages agree.
    private void PaintColumnTitles(Graphics graphics)
    {
        var rowWidth = _list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth;
        var (keyX, verdictX, docsX, reasonsX, reasonsRight) = Columns(rowWidth);
        var titles = new (string Text, int Left, int Right)[]
        {
            ("TICKET", keyX, verdictX - ColumnGap),
            (Lang.T("VERDICT", "KẾT LUẬN"), verdictX, docsX - ColumnGap),
            ("DOCS", docsX, reasonsX - ColumnGap),
            (Lang.T("REASONS", "LÝ DO"), reasonsX, reasonsRight),
        };
        foreach (var (title, left, right) in titles)
        {
            TextRenderer.DrawText(graphics, title, TaskTableHeader.TitleFont, new Rectangle(left, 0, Math.Max(0, right - left), ColumnTitlesH - 1),
                Theme.TextSecondary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        using var divider = new Pen(Theme.Divider);
        graphics.DrawLine(divider, 0, ColumnTitlesH - 1, rowWidth, ColumnTitlesH - 1);
    }

    // The text columns of a row and of the titles over it. The buttons take the same room on every row, so the
    // columns line up; Docs and Reasons share what is left.
    internal static (int KeyX, int VerdictX, int DocsX, int ReasonsX, int ReasonsRight) Columns(int rowWidth)
    {
        var verdictWidth = Enum.GetValues<CleanupVerdict>()
            .SelectMany(cv => new[] { VerdictTexts(cv).English, VerdictTexts(cv).Vietnamese })
            .Max(verdictText => TextRenderer.MeasureText(verdictText, TextFont).Width);
        var keyX = RowPad;
        var verdictX = keyX + RowColumns.KeyWidth(KeyFont) + ColumnGap;
        var docsX = verdictX + verdictWidth + ColumnGap;
        var reasonsRight = rowWidth - RowPad - ActionsWidth() - ColumnGap;
        var reasonsX = docsX + Math.Max(0, reasonsRight - docsX - ColumnGap) / 2 + ColumnGap;
        return (keyX, verdictX, docsX, reasonsX, reasonsRight);
    }

    // The widest pair a row can hold: Mine knowledge beside Remove or Trim docs, in either language.
    private static int ActionsWidth()
    {
        var removeOrTrimWidth = new[] { "Remove", "Xóa", "Trim docs", "Thu gọn docs" }.Max(RowButtonWidth);
        return RowButtonWidth("Mine knowledge") + ButtonGap + removeOrTrimWidth;
    }

    private static int RowButtonWidth(string label) =>
        Math.Max(UiScale.S(110), TextRenderer.MeasureText(label, ButtonFont).Width + UiScale.S(28));

    private static (string English, string Vietnamese) VerdictTexts(CleanupVerdict verdict) => verdict switch
    {
        CleanupVerdict.Ready => ("Ready", "Sẵn sàng"),
        CleanupVerdict.Check => ("Check", "Kiểm tra"),
        CleanupVerdict.Keep => ("Keep", "Giữ lại"),
        _ => ("No worktree", "Không có worktree"),
    };

    internal static (string Text, Color Color) VerdictCell(CleanupVerdict verdict)
    {
        var (english, vietnamese) = VerdictTexts(verdict);
        var verdictColor = verdict switch
        {
            CleanupVerdict.Ready => WeekCard.Green,
            CleanupVerdict.Check => WeekCard.Amber,
            _ => TaskRow.Gray,
        };
        return (Lang.T(english, vietnamese), verdictColor);
    }

    internal static string DocsText(WorktreeCleanupRow row)
    {
        var docsText = row.IsMined
            ? Lang.T("mined", "đã mine")
            : row.IsMerged
                ? Lang.T("merged, not mined yet", "đã merge, chưa mine")
                : Lang.T("not merged, nothing to mine yet", "chưa merge, chưa cần mine");
        if (row.SlimBytes > 0)
        {
            var megabytes = (row.SlimBytes / BytesPerMegabyte).ToString("0.#", CultureInfo.InvariantCulture);
            docsText += Lang.T($", {megabytes} MB freeable", $", giải phóng được {megabytes} MB");
        }
        return docsText;
    }

    private Panel BuildRow(WorktreeCleanupRow row, bool striped, int width)
    {
        var surface = striped ? Theme.RowStripe(Theme.CardSurface) : Theme.CardSurface;
        var rowPanel = new Panel { Size = new Size(width, RowH), BackColor = surface };
        var (keyX, verdictX, docsX, reasonsX, reasonsRight) = Columns(width);

        rowPanel.Controls.Add(CellLabel(row.TicketKey, KeyFont, Theme.TextPrimary, keyX, verdictX - ColumnGap));
        var (verdictText, verdictColor) = VerdictCell(row.Verdict);
        rowPanel.Controls.Add(CellLabel(verdictText, TextFont, verdictColor, verdictX, docsX - ColumnGap));
        rowPanel.Controls.Add(CellLabel(DocsText(row), TextFont, Theme.TextSecondary, docsX, reasonsX - ColumnGap));
        var hasError = _errors.TryGetValue(row.TicketKey, out var error);
        var detailText = hasError ? error! : string.Join("; ", row.Reasons);
        rowPanel.Controls.Add(CellLabel(detailText, TextFont, hasError ? Theme.Badge : Theme.TextSecondary, reasonsX, reasonsRight));

        // At the right, the first one at the edge.
        var buttonsLeft = width - RowPad;
        var actionButtons = new List<MacButton>();
        if (row.Verdict == CleanupVerdict.Ready || row.Verdict == CleanupVerdict.Check)
        {
            var removeButton = MacButton.Tinted(Lang.T("Remove", "Xóa"), Theme.Badge);
            removeButton.Click += async (_, _) => await RemoveRowAsync(row);
            _removeButtons[row.TicketKey] = removeButton;
            actionButtons.Add(removeButton);
        }
        else if (row.Verdict == CleanupVerdict.NoWorktree && row.SlimBytes > 0)
        {
            var slimButton = MacButton.Secondary(Lang.T("Trim docs", "Thu gọn docs"));
            slimButton.Click += async (_, _) => await SlimRowAsync(row);
            _slimButtons[row.TicketKey] = slimButton;
            actionButtons.Add(slimButton);
        }
        // Left of Remove / Trim docs. Only once merged to main or development: before that nothing is settled to mine.
        if (!row.IsMined && row.IsMerged)
        {
            var mineButton = MacButton.Secondary("Mine knowledge");
            mineButton.Click += async (_, _) => await MineRowAsync(row);
            _mineButtons[row.TicketKey] = mineButton;
            actionButtons.Add(mineButton);
        }

        foreach (var button in actionButtons)
        {
            button.Size = new Size(RowButtonWidth(button.Text), RowButtonH);
            button.Location = new Point(buttonsLeft - button.Width, (RowH - RowButtonH) / 2);
            button.Surface = () => surface;
            button.Enabled = !_isBusy;
            rowPanel.Controls.Add(button);
            buttonsLeft -= button.Width + ButtonGap;
        }
        return rowPanel;
    }

    // One cell, vertically centered in the row, cut with an ellipsis and its full text in a tooltip.
    private static Label CellLabel(string text, Font font, Color color, int left, int right)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = false,
            UseMnemonic = false,
            Font = font,
            ForeColor = color,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft,
            Bounds = new Rectangle(left, 0, Math.Max(right - left, 0), RowH),
        };
        ThemedToolTip.Track(label);
        return label;
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        foreach (var button in _removeButtons.Values.Concat(_slimButtons.Values).Concat(_mineButtons.Values))
        {
            button.Enabled = !busy;
        }

        UpdateHeader();
    }

    private async Task RemoveRowAsync(WorktreeCleanupRow row)
    {
        if (row.Verdict == CleanupVerdict.Check)
        {
            var reasons = string.Join("\n", row.Reasons.Select(r => $"- {r}"));
            var confirmed = ConfirmDialog.Ask(this, Lang.T($"Remove {row.TicketKey}?", $"Xóa {row.TicketKey}?"),
                Lang.T($"This worktree needs a check first:\n{reasons}\nRemove it anyway?", $"Worktree này cần kiểm tra trước:\n{reasons}\nVẫn xóa?"),
                Lang.T("Remove", "Xóa"), destructive: true);
            if (!confirmed)
            {
                return;
            }
        }

        SetBusy(true);
        try
        {
            await RunOnRowAsync(row, () => _remove(row));
        }
        finally
        {
            FinishAction();
        }
    }

    private async Task SlimRowAsync(WorktreeCleanupRow row)
    {
        if (!row.IsMined)
        {
            var confirmed = ConfirmDialog.Ask(this, Lang.T($"Trim {row.TicketKey}?", $"Thu gọn {row.TicketKey}?"),
                Lang.T($"{row.TicketKey} is not mined yet, so its findings may be lost. Use Mine knowledge first if it matters. Trim anyway?",
                    $"{row.TicketKey} chưa được mine nên có thể mất findings. Bấm Mine knowledge trước nếu cần. Vẫn thu gọn?"),
                Lang.T("Trim docs", "Thu gọn docs"), destructive: true);
            if (!confirmed)
            {
                return;
            }
        }

        SetBusy(true);
        try
        {
            await RunOnRowAsync(row, () => _slimDocs(row.TicketKey));
        }
        finally
        {
            FinishAction();
        }
    }

    private async Task MineRowAsync(WorktreeCleanupRow row)
    {
        SetBusy(true);
        try
        {
            await RunOnRowAsync(row, () => _mineKnowledge(row.TicketKey));
        }
        finally
        {
            FinishAction();
        }
    }

    private async Task RemoveAllReadyAsync()
    {
        var readyRows = ReadyRows();
        var confirmed = ConfirmDialog.Ask(this, Lang.T("Remove all ready worktrees?", "Xóa mọi worktree sẵn sàng?"),
            Lang.T($"Remove the worktrees of {readyRows.Count} tickets: {string.Join(", ", readyRows.Select(wcr => wcr.TicketKey))}.",
                $"Xóa worktree của {readyRows.Count} ticket: {string.Join(", ", readyRows.Select(wcr => wcr.TicketKey))}."),
            Lang.T("Remove all", "Xóa tất cả"), destructive: true);
        if (!confirmed)
        {
            return;
        }

        SetBusy(true);
        try
        {
            foreach (var row in readyRows)
            {
                // Closing the dialog stops the batch; nothing more is deleted once it is off screen.
                if (_closed)
                {
                    break;
                }

                await RunOnRowAsync(row, () => _remove(row));
            }
        }
        finally
        {
            FinishAction();
        }
    }

    private async Task SlimAllMinedAsync()
    {
        var minedRows = MinedSlimRows();
        var confirmed = ConfirmDialog.Ask(this, Lang.T("Trim all mined tickets?", "Thu gọn mọi ticket đã mine?"),
            Lang.T($"Delete the bulky docs of {minedRows.Count} mined tickets: {string.Join(", ", minedRows.Select(wcr => wcr.TicketKey))}.",
                $"Xóa docs nặng của {minedRows.Count} ticket đã mine: {string.Join(", ", minedRows.Select(wcr => wcr.TicketKey))}."),
            Lang.T("Trim all", "Thu gọn tất cả"), destructive: true);
        if (!confirmed)
        {
            return;
        }

        SetBusy(true);
        try
        {
            foreach (var row in minedRows)
            {
                if (_closed)
                {
                    break;
                }

                await RunOnRowAsync(row, () => _slimDocs(row.TicketKey));
            }
        }
        finally
        {
            FinishAction();
        }
    }

    // Runs the action, then re-reads just that ticket and swaps its row; an error stays on the row.
    private async Task RunOnRowAsync(WorktreeCleanupRow row, Func<Task<string?>> action)
    {
        var error = await action();
        if (IsDisposed)
        {
            return;
        }

        if (error == null)
        {
            _errors.Remove(row.TicketKey);
        }
        else
        {
            _errors[row.TicketKey] = error;
        }

        WorktreeCleanupRow reloaded;
        try
        {
            reloaded = await _loadRow(row.TicketKey);
        }
        catch (Exception exception)
        {
            // The row keeps its old state; the error on it says the new one could not be read.
            AppLogger.Error($"[cleanup] re-reading {row.TicketKey} failed: {exception}");
            _errors[row.TicketKey] = Lang.T("Could not re-read this ticket (see log).", "Không đọc lại được ticket này (xem log).");
            reloaded = row;
        }
        if (IsDisposed)
        {
            return;
        }

        var rowIndex = _rows.FindIndex(wcr => wcr.TicketKey == row.TicketKey);
        if (rowIndex != -1)
        {
            _rows[rowIndex] = reloaded;
        }

        RebuildList();
    }

    private void FinishAction()
    {
        if (!IsDisposed)
        {
            SetBusy(false);
        }
    }

    // ShowDialog only disposes the form after it returns, so IsDisposed is still false right after Close.
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _closed = true;
        base.OnFormClosed(e);
    }
}
