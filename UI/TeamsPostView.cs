using System.ComponentModel;
using System.Drawing;

namespace NoisLogTray;

// The ticket tab's Teams view: the post in an editor on a card, sent through the injected sender (the user's own
// workflow). The draft is read when the view is created, and again on each show while it has no edits.
internal sealed class TeamsPostView : UserControl
{
    private static readonly Font HelpFont = UiScale.Font("Segoe UI", 8.5F);
    private static readonly Font InputFont = UiScale.Font("Segoe UI", 10F);

    private static readonly int OuterGap = UiScale.S(12);
    private static readonly int Pad = UiScale.S(16);
    private static readonly int MaxCardW = UiScale.S(960);
    private static readonly int LineH = UiScale.S(20);
    private static readonly int ButtonH = UiScale.S(32);
    private static readonly int ButtonGap = UiScale.S(8);
    private static readonly Color ErrorColor = Color.FromArgb(230, 76, 76);

    private readonly string _ticketKey;
    private readonly Func<string> _loadDraft;
    private readonly Func<DateTimeOffset?> _postedAt;
    private readonly Func<string, CancellationToken, Task<bool>> _send;
    private readonly Card _card = new();
    private readonly Label _help = new();
    private readonly Label _posted = new();
    private readonly RoundedHost _host = new();
    private readonly TextBox _box = new();
    private readonly Label _status = new();
    private readonly MacButton _reloadButton = MacButton.Secondary(string.Empty);
    private readonly MacButton _sendButton = MacButton.Primary(string.Empty);
    private string _loadedDraft = "";
    private bool _repostArmed;
    private bool _caretPlaced;
    private CancellationTokenSource? _sending;

    // A send went through; TrayApp records when and tells the user.
    internal event Action? Posted;

    internal TeamsPostView(string ticketKey, Func<string> loadDraft, Func<DateTimeOffset?> postedAt,
        Func<string, CancellationToken, Task<bool>> send)
    {
        _ticketKey = ticketKey;
        _loadDraft = loadDraft;
        _postedAt = postedAt;
        _send = send;
        BackColor = Theme.WindowBg;

        foreach (var label in new[] { _help, _posted, _status })
        {
            label.AutoSize = false;
            ThemedToolTip.Track(label);
            label.Font = HelpFont;
            label.BackColor = Color.Transparent;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.UseMnemonic = false;
        }
        _box.Multiline = true;
        _box.AcceptsReturn = true; // Enter inserts a newline; Ctrl+Enter sends
        _box.ScrollBars = ScrollBars.Vertical;
        _box.BorderStyle = BorderStyle.None;
        _box.Font = InputFont;
        _box.KeyDown += OnBoxKeyDown;
        _host.Controls.Add(_box);
        ThemedScrollBar.Attach(_box);
        _reloadButton.Click += (_, _) => LoadDraft();
        _sendButton.Click += async (_, _) => await SendAsync();
        _card.Controls.AddRange(new Control[] { _help, _posted, _host, _status, _reloadButton, _sendButton });
        Controls.Add(_card);

        LoadDraft();
        ApplyTheme();
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
            // A tab closed mid-send drops the request.
            _sending?.Cancel();
            _sending?.Dispose();
        }
        base.Dispose(disposing);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string PostText
    {
        get { return _box.Text; }
        set { _box.Text = value; }
    }

    internal string StatusText
    {
        get { return _status.Text; }
    }

    // Shown again: an untouched draft follows the file (Claude may have rewritten docs\<KEY>\teams-post.md).
    internal void Activate()
    {
        if (_sending == null && _box.Text == _loadedDraft) LoadDraft();
        ShowPosted();
        if (!IsHandleCreated || !Visible) return;
        _box.Focus();
        // A TextBox focused for the first time selects everything, so the first keystroke would wipe the title.
        if (!_caretPlaced)
        {
            _box.Select(TeamsPost.DescriptionStart(_box.Text), 0);
            _caretPlaced = true;
        }
    }

    private void LoadDraft()
    {
        _loadedDraft = _loadDraft();
        _box.Text = _loadedDraft;
        _caretPlaced = false;
        _repostArmed = false;
        ShowStatus("", Theme.TextSecondary);
    }

    private void OnBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;
            _ = SendAsync();
        }
        else if (e.Control && e.KeyCode == Keys.A)
        {
            // A multiline TextBox has no Ctrl+A of its own.
            e.SuppressKeyPress = true;
            _box.SelectAll();
        }
    }

    internal async Task SendAsync()
    {
        if (_sending != null) return;
        var text = _box.Text.Trim();
        if (text.Length == 0)
        {
            ShowStatus(Lang.T("The post is empty.", "Bài đang trống."), ErrorColor);
            return;
        }
        // The workflow's Message field cannot be empty, and only the first line goes to the subject.
        if (TeamsPost.SplitSubject(text).Body.Length == 0)
        {
            ShowStatus(Lang.T("Write the post below the title line.", "Viết nội dung bên dưới dòng tiêu đề."), ErrorColor);
            return;
        }
        // Already posted: the first Send only warns, a second one posts again.
        var postedAt = _postedAt();
        if (postedAt != null && !_repostArmed)
        {
            _repostArmed = true;
            ShowStatus(Lang.T($"{_ticketKey} was already posted on {PostedWhen(postedAt.Value)}. Send again to post it again.",
                $"{_ticketKey} đã đăng lúc {PostedWhen(postedAt.Value)}. Bấm Gửi lần nữa để đăng lại."), ErrorColor);
            return;
        }

        _repostArmed = false;
        _sending = new CancellationTokenSource();
        SetSending(true);
        bool sent;
        var busyWork = BusyWork.Shared.Begin(Lang.T($"Posting {_ticketKey} to Teams...", $"Đang đăng {_ticketKey} lên Teams..."));
        try
        {
            sent = await _send(text, _sending.Token);
        }
        finally
        {
            busyWork.Dispose();
            _sending.Dispose();
            _sending = null;
        }
        if (IsDisposed) return;

        SetSending(false);
        if (!sent)
        {
            ShowStatus(Lang.T("Could not send (see log). Check the workflow URL in Settings.",
                "Gửi không được (xem log). Kiểm tra URL Workflow trong Settings."), ErrorColor);
            return;
        }
        Posted?.Invoke();
        ShowPosted();
        ShowStatus(Lang.T("Posted.", "Đã đăng."), WeekCard.Green);
    }

    private void SetSending(bool sending)
    {
        _sendButton.Enabled = !sending;
        _reloadButton.Enabled = !sending;
        _box.ReadOnly = sending;
        ShowStatus(sending ? Lang.T("Sending...", "Đang gửi...") : "", Theme.TextSecondary);
        // Disabling Send must not leave focus on Reload, where a stray Enter would replace the post.
        if (sending && _box.CanFocus) _box.Focus();
    }

    private void ShowStatus(string text, Color color)
    {
        _status.ForeColor = color;
        _status.Text = text;
    }

    private void ShowPosted()
    {
        var postedAt = _postedAt();
        _posted.Text = postedAt == null
            ? ""
            : Lang.T($"Already posted on {PostedWhen(postedAt.Value)}.", $"Đã đăng lúc {PostedWhen(postedAt.Value)}.");
        PerformLayout(); // the line shows only once posted
    }

    private static string PostedWhen(DateTimeOffset postedAt) =>
        $"{Lang.ShortDate(postedAt.DateTime)} {postedAt:HH:mm}";

    // The card fills the view up to MaxCardW: help and posted lines, the editor, then the status and the buttons.
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var cardW = Math.Min(MaxCardW, Math.Max(0, ClientSize.Width - 2 * OuterGap));
        var cardH = Math.Max(0, ClientSize.Height - 2 * OuterGap);
        _card.SetBounds(OuterGap, OuterGap, cardW, cardH);

        var innerW = Math.Max(0, cardW - 2 * Pad);
        _help.SetBounds(Pad, Pad, innerW, LineH);
        var y = _help.Bottom;
        _posted.Visible = _posted.Text.Length != 0;
        if (_posted.Visible)
        {
            _posted.SetBounds(Pad, y, innerW, LineH);
            y = _posted.Bottom;
        }
        y += UiScale.S(6);

        var buttonY = cardH - Pad - ButtonH;
        _host.SetBounds(Pad, y, innerW, Math.Max(UiScale.S(60), buttonY - UiScale.S(10) - y));
        _box.SetBounds(UiScale.S(10), UiScale.S(8), Math.Max(0, _host.Width - UiScale.S(20)), Math.Max(0, _host.Height - UiScale.S(16)));

        _sendButton.SetBounds(Pad + innerW - _sendButton.Width, buttonY, _sendButton.Width, ButtonH);
        _reloadButton.SetBounds(_sendButton.Left - ButtonGap - _reloadButton.Width, buttonY, _reloadButton.Width, ButtonH);
        _status.SetBounds(Pad, buttonY, Math.Max(0, _reloadButton.Left - ButtonGap - Pad), ButtonH);
    }

    private void ApplyTheme()
    {
        BackColor = Theme.WindowBg;
        _help.ForeColor = Theme.TextSecondary;
        _posted.ForeColor = Theme.TextSecondary;
        _box.BackColor = Theme.InputBg;
        _box.ForeColor = Theme.TextPrimary;
    }

    private void ApplyLanguage()
    {
        _help.Text = Lang.T(
            "The first line becomes the subject. Sent through your workflow, so it shows under your name. Ctrl+Enter sends.",
            "Dòng đầu là tiêu đề (Subject). Gửi qua Workflow của bạn nên hiện dưới tên bạn. Ctrl+Enter để gửi.");
        _sendButton.Text = Lang.T("Send", "Gửi");
        _sendButton.AccessibleName = Lang.T($"Send {_ticketKey} to Teams", $"Gửi {_ticketKey} lên Teams");
        _reloadButton.Text = Lang.T("Reload draft", "Tải lại nháp");
        _reloadButton.AccessibleName = Lang.T("Reload the draft (drops your edits)", "Tải lại bản nháp (bỏ phần đã sửa)");
        foreach (var button in new[] { _sendButton, _reloadButton })
            button.Width = new[] { "Send", "Gửi", "Reload draft", "Tải lại nháp" }
                .Max(label => TextRenderer.MeasureText(label, button.Font).Width) + UiScale.S(28);
        ShowPosted();
    }
}
