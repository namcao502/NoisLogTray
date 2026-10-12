using System.ComponentModel;
using System.Drawing;

namespace NoisLogTray;

// Themed dialog to review and edit a ticket's Teams post, then send it through the injected sender.
// Closes with OK once the sender reports success; closing it while a send is in flight cancels that send.
internal sealed class TeamsPostForm : Form
{
    private static readonly Font TitleFont = UiScale.Font("Segoe UI Semibold", 13F, FontStyle.Bold);
    private static readonly Font HelpFont = UiScale.Font("Segoe UI", 8.5F);
    private static readonly Font InputFont = UiScale.Font("Segoe UI", 9.5F);

    private static readonly int Pad = UiScale.S(24);
    private static readonly int FieldW = UiScale.S(592); // ClientWidth (640 logical) - 2*Pad
    private static readonly int FieldH = UiScale.S(320);
    private static readonly int ButtonH = UiScale.S(36);
    private static readonly int SendW = UiScale.S(110);
    private static readonly Color ErrorColor = Color.FromArgb(230, 76, 76);

    private readonly TextBox _box = new();
    private readonly Label _error = new();
    private readonly string _ticketKey;
    private readonly DateTimeOffset? _postedAt;
    private readonly string _postedWhen;
    private readonly Func<string, CancellationToken, Task<bool>> _send;
    private MacButton _sendButton = null!;
    private MacButton _cancelButton = null!;
    private CancellationTokenSource? _sending;

    internal TeamsPostForm(string ticketKey, string draft, DateTimeOffset? postedAt,
        Func<string, CancellationToken, Task<bool>> send)
    {
        _ticketKey = ticketKey;
        _postedAt = postedAt;
        _postedWhen = postedAt == null ? "" : $"{Lang.ShortDate(postedAt.Value.DateTime)} {postedAt.Value:HH:mm}";
        _send = send;

        Text = Lang.T($"Post {ticketKey} to Teams", $"Đăng {ticketKey} lên Teams");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.WindowBg;
        Icon = AppIcon.Load(32);

        var y = Pad;
        y = AddHeader(y);
        y = AddField(y, draft);
        AddErrorAndButtons(y);

        // A focused TextBox selects everything, so the first keystroke would wipe the title.
        Shown += (_, _) =>
        {
            _box.Focus();
            _box.Select(TeamsPost.DescriptionStart(_box.Text), 0);
        };
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string PostText
    {
        get { return _box.Text; }
        set { _box.Text = value; }
    }

    internal string ErrorText
    {
        get { return _error.Text; }
    }

    private int AddHeader(int y)
    {
        var title = new Label
        {
            Text = Text,
            AutoSize = true,
            Location = new Point(Pad, y),
            Font = TitleFont,
            ForeColor = Theme.TextPrimary,
            BackColor = Color.Transparent,
        };
        Controls.Add(title);
        y += UiScale.S(28);

        var help = new Label
        {
            Text = Lang.T(
                "The first line becomes the subject. Sent through your workflow, so it shows under your name.",
                "Dòng đầu là tiêu đề (Subject). Gửi qua Workflow của bạn nên hiện dưới tên bạn."),
            AutoSize = false,
            Size = new Size(FieldW, UiScale.S(18)),
            Location = new Point(Pad, y),
            Font = HelpFont,
            ForeColor = Theme.TextSecondary,
            BackColor = Color.Transparent,
        };
        Controls.Add(help);
        y += UiScale.S(22);

        if (_postedAt != null)
        {
            var posted = new Label
            {
                Text = Lang.T($"Already posted on {_postedWhen}.", $"Đã đăng lúc {_postedWhen}."),
                AutoSize = false,
                Size = new Size(FieldW, UiScale.S(18)),
                Location = new Point(Pad, y),
                Font = HelpFont,
                ForeColor = Theme.TextSecondary,
                BackColor = Color.Transparent,
            };
            Controls.Add(posted);
            y += UiScale.S(22);
        }
        return y + UiScale.S(4);
    }

    private int AddField(int y, string draft)
    {
        var host = new RoundedHost { Location = new Point(Pad, y), Size = new Size(FieldW, FieldH) };
        _box.Multiline = true;
        _box.AcceptsReturn = true; // Enter inserts a newline rather than sending
        _box.ScrollBars = ScrollBars.Vertical;
        _box.BorderStyle = BorderStyle.None;
        _box.Font = InputFont;
        _box.BackColor = Theme.InputBg;
        _box.ForeColor = Theme.TextPrimary;
        _box.Text = draft;
        _box.SetBounds(UiScale.S(10), UiScale.S(8), host.Width - UiScale.S(20), host.Height - UiScale.S(16));
        host.Controls.Add(_box);
        Controls.Add(host);
        return y + FieldH + UiScale.S(12);
    }

    private void AddErrorAndButtons(int y)
    {
        _error.AutoSize = false;
        _error.Size = new Size(FieldW, UiScale.S(18));
        _error.Location = new Point(Pad, y);
        _error.Font = HelpFont;
        _error.BackColor = Color.Transparent;
        _error.TextAlign = ContentAlignment.MiddleLeft;
        Controls.Add(_error);
        y += UiScale.S(24);

        _sendButton = MacButton.Primary(Lang.T("Send", "Gửi"));
        _sendButton.OnWindow = true;
        _sendButton.Size = new Size(SendW, ButtonH);
        _sendButton.Location = new Point(Pad + FieldW - SendW, y);
        _sendButton.Click += OnSendClicked;

        _cancelButton = MacButton.Secondary(Lang.T("Cancel", "Hủy"));
        _cancelButton.OnWindow = true;
        var cancelW = UiScale.S(100);
        _cancelButton.Size = new Size(cancelW, ButtonH);
        _cancelButton.Location = new Point(Pad + FieldW - SendW - UiScale.S(12) - cancelW, y);
        _cancelButton.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        Controls.Add(_sendButton);
        Controls.Add(_cancelButton);
        y += ButtonH + Pad;

        CancelButton = _cancelButton; // Esc closes; Enter is left to the multiline box (no AcceptButton)
        ClientSize = new Size(FieldW + 2 * Pad, y);
    }

    private async void OnSendClicked(object? sender, EventArgs e)
    {
        await SendAsync();
    }

    // Cancel stays enabled while sending: closing (Cancel, Esc, X) cancels the request in OnFormClosing.
    internal async Task SendAsync()
    {
        var text = _box.Text.Trim();
        if (text.Length == 0)
        {
            ShowError(Lang.T("The post is empty.", "Bài đang trống."));
            return;
        }
        // The workflow's Message field cannot be empty, and only the first line goes to the subject.
        if (TeamsPost.SplitSubject(text).Body.Length == 0)
        {
            ShowError(Lang.T("Write the post below the title line.", "Viết nội dung bên dưới dòng tiêu đề."));
            return;
        }
        if (_postedAt != null && MessageBox.Show(this,
                Lang.T($"{_ticketKey} was already posted on {_postedWhen}. Post again?",
                    $"{_ticketKey} đã đăng lúc {_postedWhen}. Đăng lại?"),
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        _sending?.Dispose();
        _sending = new CancellationTokenSource();
        SetSending(true);
        // Disabling Send moves focus to Cancel, where a stray Enter or Space would cancel the post.
        ActiveControl = null;
        var sent = await _send(text, _sending.Token);
        if (IsDisposed || _sending.IsCancellationRequested)
        {
            return;
        }

        SetSending(false);
        if (sent)
        {
            DialogResult = DialogResult.OK;
            return;
        }
        ShowError(Lang.T(
            "Could not send (see log). Check the workflow URL in Settings.",
            "Gửi không được (xem log). Kiểm tra URL Workflow trong Settings."));
        _box.Focus();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _sending?.Cancel();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _sending?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void ShowError(string message)
    {
        _error.ForeColor = ErrorColor;
        _error.Text = message;
    }

    private void SetSending(bool sending)
    {
        _sendButton.Enabled = !sending;
        _box.Enabled = !sending;
        _error.ForeColor = Theme.TextSecondary;
        _error.Text = sending ? Lang.T("Sending...", "Đang gửi...") : "";
    }
}
