using System.Diagnostics;
using System.Drawing;
using System.Globalization;

namespace NoisLogTray;

// The Settings body, shared by the dialog and the shell's Settings page. Pills apply instantly;
// the account fields are verified and raised on Save for AppConfig.SaveUserConfig.
internal sealed class SettingsPanel : UserControl
{
    private static readonly Font LabelFont = UiScale.Font("Segoe UI", 9F);
    private static readonly Font HelpFont = UiScale.Font("Segoe UI", 8.5F);
    private static readonly Font InputFont = UiScale.Font("Segoe UI", 9.5F);
    private static readonly Font PillFont = UiScale.Font("Segoe UI", 9.5F);
    private static readonly Color ErrorColor = Color.FromArgb(230, 76, 76);

    internal static readonly int ClientW = UiScale.S(400); // the dialog's width; the shell page passes its own
    internal static readonly int WideMinWidth = UiScale.S(700); // from here the options and the Jira site/email share a row
    private static readonly int Pad = UiScale.S(20);
    private readonly int _innerW;
    private readonly bool _wide;
    private static readonly int RowH = UiScale.S(30); // pills, inputs and buttons share one height, as in the main window
    private static readonly int Gap = UiScale.S(8);
    private static readonly int CaptionH = UiScale.S(18); // a field's caption, above its input

    private readonly Dictionary<string, TextBox> _fields = new();
    private readonly Label _status = new();
    private readonly List<(Control Control, string English, string Vietnamese)> _texts = new();
    private readonly List<Label> _primaryLabels = new();
    private readonly List<Label> _secondaryLabels = new();
    private readonly List<Panel> _dividers = new();
    private readonly MacButton _english = MacButton.Secondary("English");
    private readonly MacButton _vietnamese = MacButton.Secondary("Tiếng Việt");
    private readonly MacButton _startupOn = MacButton.Secondary("");
    private readonly MacButton _startupOff = MacButton.Secondary("");
    private readonly MacButton[] _zoomPills = UiScale.ZoomChoices.Select(percent => MacButton.Secondary($"{percent}%")).ToArray();
    private readonly MacButton _save = MacButton.Primary("");
    private bool _statusIsError;
    private int _y;

    internal bool IsBusy { get; private set; }

    // Raised as the very last statement of a successful Save; the host may dispose the panel in response.
    internal event Action<IReadOnlyDictionary<string, string>>? SaveRequested;

    // width is in physical pixels; null = the dialog's ClientW.
    internal SettingsPanel(IReadOnlyDictionary<string, string> initial, int? width = null)
    {
        var panelW = width ?? ClientW;
        _y = Pad;
        _innerW = panelW - 2 * Pad;
        _wide = panelW >= WideMinWidth;
        BackColor = Theme.CardSurface;

        if (_wide)
        {
            // The three option groups share one row, a third of the width each.
            var y = _y;
            var column = (_innerW + Gap) / 3;
            AddOptionGroup("Language", "Ngôn ngữ", Pad, _vietnamese, _english);
            _y = y;
            AddOptionGroup("Start with Windows", "Khởi động cùng Windows", Pad + column, _startupOn, _startupOff);
            _y = y;
            AddOptionGroup("Zoom", "Thu phóng", Pad + 2 * column, _zoomPills);
        }
        else
        {
            AddOptionGroup("Language", "Ngôn ngữ", Pad, _vietnamese, _english);
            AddOptionGroup("Start with Windows", "Khởi động cùng Windows", Pad, _startupOn, _startupOff);
            AddOptionGroup("Zoom", "Thu phóng", Pad, _zoomPills);
        }
        _english.Click += (_, _) => SetLanguage(vietnamese: false);
        _vietnamese.Click += (_, _) => SetLanguage(vietnamese: true);
        _startupOn.Click += (_, _) => SetStartup(true);
        _startupOff.Click += (_, _) => SetStartup(false);
        for (var index = 0; index < _zoomPills.Length; index++)
        {
            var percent = UiScale.ZoomChoices[index];
            _zoomPills[index].Click += (_, _) => SetZoom(percent);
        }
        AddDivider();

        AddLabel("Account - applies when you press Save", "Tài khoản - áp dụng khi bấm Lưu", LabelFont, _primaryLabels);
        AddHelp();
        if (_wide)
        {
            AddFieldPair(
                ("Jira site URL", "Địa chỉ Jira", "JIRA_BASE_URL", false),
                ("Jira email", "Email Jira", "JIRA_EMAIL", false));
        }
        else
        {
            AddField("Jira site URL", "Địa chỉ Jira", "JIRA_BASE_URL", secret: false);
            AddField("Jira email", "Email Jira", "JIRA_EMAIL", secret: false);
        }
        // Short values share a row to keep the card compact.
        AddFieldPair(
            ("Jira API token", "API token Jira", "JIRA_API_TOKEN", true),
            ("HRM API key", "API key HRM", "HRM_API_KEY", true));
        AddFieldPair(
            ("TSC columns (e.g. M, J)", "Cột TSC (vd. M, J)", "TSC_GRAPH_COLUMNS", false),
            ("Daily log time (e.g. 6:00 PM)", "Giờ tự log (vd. 6:00 PM)", "LOG_TIME", false));
        AddFolderField("Dev repo path", "Đường dẫn repo dev", "DEV_REPO_PATH");
        AddFieldPair(
            ("Azure DevOps PAT", "PAT Azure DevOps", "AZURE_DEVOPS_PAT", true),
            ("Teams webhook URL", "URL webhook Teams", "TEAMS_WEBHOOK_URL", true));
        AddField("Teams post workflow URL (posts as you)", "URL Workflow đăng Teams (tên bạn)", "TEAMS_POST_WORKFLOW_URL", secret: true);
        AddStatusAndSave();
        AddDivider();
        AddFooter();

        Size = new Size(panelW, _y + Pad);
        LoadValues(initial);
        ApplyLanguage();
        ShowSelections();
        Theme.Changed += ApplyTheme;
        Lang.Changed += OnLangChanged;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= ApplyTheme;
            Lang.Changed -= OnLangChanged;
        }
        base.Dispose(disposing);
    }

    internal void LoadValues(IReadOnlyDictionary<string, string> values)
    {
        ShowSelections();
        _fields["JIRA_BASE_URL"].Text = Value(values, "JIRA_BASE_URL", AppConfig.DefaultJiraBaseUrl);
        _fields["JIRA_EMAIL"].Text = Value(values, "JIRA_EMAIL", "");
        _fields["JIRA_API_TOKEN"].Text = Value(values, "JIRA_API_TOKEN", "");
        _fields["HRM_API_KEY"].Text = Value(values, "HRM_API_KEY", "");
        _fields["TSC_GRAPH_COLUMNS"].Text = Value(values, "TSC_GRAPH_COLUMNS", "M, J");
        _fields["LOG_TIME"].Text = AppConfig.ParseLogTime(Value(values, "LOG_TIME", ""))
            .ToString("h:mm tt", CultureInfo.InvariantCulture);
        _fields["DEV_REPO_PATH"].Text = Value(values, "DEV_REPO_PATH", AppConfig.DefaultDevRepoPath);
        _fields["AZURE_DEVOPS_PAT"].Text = Value(values, "AZURE_DEVOPS_PAT", "");
        _fields["TEAMS_WEBHOOK_URL"].Text = Value(values, "TEAMS_WEBHOOK_URL", "");
        _fields["TEAMS_POST_WORKFLOW_URL"].Text = Value(values, "TEAMS_POST_WORKFLOW_URL", "");
        _statusIsError = false;
        _status.Text = "";
    }

    internal void FocusFirstField() => _fields["JIRA_EMAIL"].Focus();

    // What is typed now, saved or not; the page carries it over when it rebuilds the panel at a new width.
    internal IReadOnlyDictionary<string, string> CurrentValues() =>
        _fields.ToDictionary(field => field.Key, field => field.Value.Text);

    private static string Value(IReadOnlyDictionary<string, string> values, string key, string fallback) =>
        values.TryGetValue(key, out var v) && v.Length != 0 ? v : fallback;

    // A muted caption over a row of pills; the selected pill reads as the primary button.
    private void AddOptionGroup(string english, string vietnamese, int left, params MacButton[] pills)
    {
        AddLabelAt(left, english, vietnamese, LabelFont, _secondaryLabels);
        var x = left;
        foreach (var pill in pills)
        {
            pill.Font = PillFont;
            pill.Location = new Point(x, _y);
            pill.Size = new Size(PillWidth(pill), RowH);
            Controls.Add(pill);
            x += pill.Width + Gap;
        }
        _y += RowH + UiScale.S(14);
    }

    // Wide enough for the pill's text in either language.
    private static int PillWidth(MacButton pill)
    {
        if (pill.Text.Length != 0) return TextRenderer.MeasureText(pill.Text, PillFont).Width + UiScale.S(28);
        return UiScale.S(64); // On/Off pills: "On"/"Bật", "Off"/"Tắt"
    }

    private Label NewLabel(Font font, List<Label> role)
    {
        var label = new Label
        {
            AutoSize = true,
            Font = font,
            ForeColor = role == _primaryLabels ? Theme.TextPrimary : Theme.TextSecondary,
            BackColor = Color.Transparent,
        };
        role.Add(label);
        Controls.Add(label);
        return label;
    }

    private void AddLabel(string english, string vietnamese, Font font, List<Label> role) =>
        AddLabelAt(Pad, english, vietnamese, font, role);

    private void AddLabelAt(int x, string english, string vietnamese, Font font, List<Label> role)
    {
        var label = NewLabel(font, role);
        label.Location = new Point(x, _y);
        _texts.Add((label, english, vietnamese));
        _y += UiScale.S(20);
    }

    private void AddHelp()
    {
        var help = NewLabel(HelpFont, _secondaryLabels);
        help.AutoSize = false;
        help.Size = new Size(_innerW, CaptionH);
        help.Location = new Point(Pad, _y);
        _texts.Add((help,
            "Stored only on this PC. Get a Jira token at id.atlassian.com.",
            "Chỉ lưu trên máy này. Lấy Jira token tại id.atlassian.com."));
        _y += UiScale.S(24);
    }

    private void AddField(string english, string vietnamese, string key, bool secret)
    {
        AddFieldAt(Pad, _innerW, english, vietnamese, key, secret);
        _y += CaptionH + RowH + UiScale.S(10);
    }

    // A path field with a "Browse..." button that opens a folder picker.
    private void AddFolderField(string english, string vietnamese, string key)
    {
        var browseW = ButtonWidths.ToolbarWidth(PillFont);
        AddFieldAt(Pad, _innerW - browseW - Gap, english, vietnamese, key, secret: false);
        var browse = MacButton.Secondary("");
        browse.Font = PillFont;
        browse.Bounds = new Rectangle(Pad + _innerW - browseW, _y + CaptionH, browseW, RowH);
        browse.Click += (_, _) => PickFolder(_fields[key]);
        Controls.Add(browse);
        _texts.Add((browse, "Browse...", "Chọn..."));
        _y += CaptionH + RowH + UiScale.S(10);
    }

    private void PickFolder(TextBox box)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = Lang.T("Choose the dev repo folder", "Chọn thư mục repo dev"),
            UseDescriptionForTitle = true,
        };
        if (Directory.Exists(box.Text.Trim())) dialog.InitialDirectory = box.Text.Trim();
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK) box.Text = dialog.SelectedPath;
    }

    private void AddFieldPair(
        (string English, string Vietnamese, string Key, bool Secret) left,
        (string English, string Vietnamese, string Key, bool Secret) right)
    {
        var width = (_innerW - Gap) / 2;
        AddFieldAt(Pad, width, left.English, left.Vietnamese, left.Key, left.Secret);
        AddFieldAt(Pad + width + Gap, width, right.English, right.Vietnamese, right.Key, right.Secret);
        _y += CaptionH + RowH + UiScale.S(10);
    }

    // A muted caption with a rounded input under it, at the current row.
    private void AddFieldAt(int x, int width, string english, string vietnamese, string key, bool secret)
    {
        var caption = NewLabel(HelpFont, _secondaryLabels);
        caption.Location = new Point(x, _y);
        _texts.Add((caption, english, vietnamese));

        var host = new RoundedHost { Location = new Point(x, _y + CaptionH), Size = new Size(width, RowH) };
        var box = new TextBox
        {
            BorderStyle = BorderStyle.None,
            Font = InputFont,
            BackColor = Theme.InputBg,
            ForeColor = Theme.TextPrimary,
            UseSystemPasswordChar = secret,
        };
        var h = box.PreferredHeight;
        box.SetBounds(UiScale.S(10), (host.Height - h) / 2, host.Width - UiScale.S(20), h);
        box.KeyDown += OnFieldKeyDown;
        host.Controls.Add(box);
        Controls.Add(host);
        _fields[key] = box;
    }

    private void AddStatusAndSave()
    {
        var saveW = UiScale.S(100);
        _status.AutoSize = false;
        _status.Size = new Size(_innerW - saveW - Gap, RowH);
        _status.Location = new Point(Pad, _y);
        _status.Font = HelpFont;
        _status.BackColor = Color.Transparent;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        Controls.Add(_status);

        _save.Size = new Size(saveW, RowH);
        _save.Location = new Point(Pad + _innerW - saveW, _y);
        _save.Click += OnSave;
        Controls.Add(_save);
        _texts.Add((_save, "Save", "Lưu"));
        _y += RowH + UiScale.S(14);
    }

    private void AddDivider()
    {
        var divider = new Panel { Location = new Point(Pad, _y), Size = new Size(_innerW, 1), BackColor = Theme.CardBorder };
        _dividers.Add(divider);
        Controls.Add(divider);
        _y += UiScale.S(14);
    }

    private void AddFooter()
    {
        var logs = MacButton.Secondary("");
        logs.Size = new Size(Math.Max(TextRenderer.MeasureText("Open log folder", PillFont).Width,
            TextRenderer.MeasureText("Mở thư mục log", PillFont).Width) + UiScale.S(28), RowH);
        logs.Location = new Point(Pad, _y);
        logs.Click += (_, _) =>
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            Process.Start(new ProcessStartInfo(AppPaths.LogDirectory) { UseShellExecute = true });
        };
        Controls.Add(logs);
        _texts.Add((logs, "Open log folder", "Mở thư mục log"));
        _y += RowH;
    }

    private void OnLangChanged()
    {
        ApplyLanguage();
        ShowSelections(); // the language pills follow a toggle made from the header too
    }

    private void ApplyLanguage()
    {
        foreach (var (control, english, vietnamese) in _texts) control.Text = Lang.T(english, vietnamese);
        _startupOn.Text = Lang.T("On", "Bật");
        _startupOff.Text = Lang.T("Off", "Tắt");
    }

    private void ApplyTheme()
    {
        BackColor = Theme.CardSurface;
        foreach (var label in _primaryLabels) label.ForeColor = Theme.TextPrimary;
        foreach (var label in _secondaryLabels) label.ForeColor = Theme.TextSecondary;
        foreach (var box in _fields.Values)
        {
            box.BackColor = Theme.InputBg;
            box.ForeColor = Theme.TextPrimary;
        }
        foreach (var divider in _dividers) divider.BackColor = Theme.CardBorder;
        if (!_statusIsError) _status.ForeColor = Theme.TextSecondary;
        Invalidate(true);
    }

    private void ShowSelections()
    {
        _english.Selected = !Lang.Vietnamese;
        _vietnamese.Selected = Lang.Vietnamese;
        var startup = StartupService.IsEnabled();
        _startupOn.Selected = startup;
        _startupOff.Selected = !startup;
        var zoom = SavedZoomPercent();
        for (var index = 0; index < _zoomPills.Length; index++)
            _zoomPills[index].Selected = UiScale.ZoomChoices[index] == zoom;
    }

    // The saved choice, not UiScale.Factor: a pick made this session shows before the restart applies it.
    private static int SavedZoomPercent()
    {
        var saved = AppSettings.Load().UiZoomPercent;
        return Array.IndexOf(UiScale.ZoomChoices, saved) >= 0 ? saved : UiScale.DefaultZoomPercent;
    }

    private void SetZoom(int percent)
    {
        var settings = AppSettings.Load(); // read-modify-write to keep other keys
        settings.UiZoomPercent = percent;
        AppSettings.Save(settings);
        ShowSelections();
        if (UiScale.ToFactor(percent) != UiScale.Factor)
            ShowInfo(Lang.T("Zoom applies after restarting the app.", "Thu phóng áp dụng sau khi mở lại app."));
        else
            ShowInfo("");
    }

    private void SetLanguage(bool vietnamese)
    {
        if (Lang.Vietnamese != vietnamese) Lang.Toggle(); // re-labels this panel and the app
        ShowSelections();
    }

    private void SetStartup(bool enable)
    {
        var result = StartupService.TrySet(enable);
        ShowSelections(); // reflects the real state, so a failed change snaps back
        if (result.Success)
        {
            ShowInfo(enable
                ? Lang.T("Will start with Windows.", "Sẽ khởi động cùng Windows.")
                : Lang.T("Won't start with Windows.", "Sẽ không khởi động cùng Windows."));
        }
        else
        {
            Fail(result.ErrorMessage ?? Lang.T("Startup change failed.", "Không đổi được chế độ khởi động."));
        }
    }

    private void OnFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter) return;
        e.SuppressKeyPress = true; // no ding from the single-line box
        if (!IsBusy) OnSave(sender, e);
    }

    private async void OnSave(object? sender, EventArgs e)
    {
        var baseUrl = _fields["JIRA_BASE_URL"].Text.Trim();
        var email = _fields["JIRA_EMAIL"].Text.Trim();
        var token = _fields["JIRA_API_TOKEN"].Text.Trim();
        var hrmKey = _fields["HRM_API_KEY"].Text.Trim();
        var columns = TscCells.ParseColumns(_fields["TSC_GRAPH_COLUMNS"].Text);
        var logTimeText = _fields["LOG_TIME"].Text.Trim();

        var formatError = SettingsValidation.FormatError(baseUrl, email, token, hrmKey, columns, logTimeText);
        if (formatError != null)
        {
            Fail(formatError);
            return;
        }
        var logTime = AppConfig.ParseLogTime(logTimeText); // FormatError already proved it parses

        // Verify the credentials actually work before saving, so a wrong token/key
        // is caught here rather than silently failing later.
        CredentialCheck jira, hrm;
        SetBusy(true, Lang.T("Verifying credentials...", "Đang kiểm tra thông tin đăng nhập..."));
        try
        {
            var jiraTask = new JiraClient(baseUrl, email, token).ValidateAsync();
            var hrmTask = HrmMcpClient.ValidateAsync(hrmKey);
            await Task.WhenAll(jiraTask, hrmTask);
            jira = jiraTask.Result;
            hrm = hrmTask.Result;
        }
        finally
        {
            if (!IsDisposed) SetBusy(false, "");
        }
        // The host may have closed the dialog or rebuilt the shell mid-verify; never save abandoned values.
        if (IsDisposed) return;

        if (jira == CredentialCheck.Rejected)
        {
            Fail(Lang.T("Jira rejected your email or API token.", "Jira từ chối email hoặc API token của bạn."));
            return;
        }
        if (hrm == CredentialCheck.Rejected)
        {
            Fail(Lang.T("HRM rejected your API key.", "HRM từ chối API key của bạn."));
            return;
        }

        // Could not reach a service to confirm: let the user save anyway (they may be
        // offline) rather than lock them out, but make it a deliberate choice.
        if (jira == CredentialCheck.Unreachable || hrm == CredentialCheck.Unreachable)
        {
            var which = jira == CredentialCheck.Unreachable && hrm == CredentialCheck.Unreachable
                ? Lang.T("Jira and the HRM server", "Jira và máy chủ HRM")
                : jira == CredentialCheck.Unreachable ? "Jira" : Lang.T("the HRM server", "máy chủ HRM");
            var answer = MessageBox.Show(FindForm(),
                Lang.T($"Could not reach {which} to verify your details (are you online?).\n\nSave anyway?",
                    $"Không kết nối được {which} để kiểm tra thông tin (bạn có đang online không?).\n\nVẫn lưu?"),
                Lang.T("Couldn't verify", "Không kiểm tra được"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes) return;
            if (IsDisposed) return; // the host closed the dialog or rebuilt the shell while the box was open
        }

        var values = new Dictionary<string, string>
        {
            ["JIRA_BASE_URL"] = baseUrl,
            ["JIRA_EMAIL"] = email,
            ["JIRA_API_TOKEN"] = token,
            ["HRM_API_KEY"] = hrmKey,
            ["TSC_GRAPH_COLUMNS"] = string.Join(", ", columns),
            ["LOG_TIME"] = logTime.ToString("h:mm tt", CultureInfo.InvariantCulture),
            ["DEV_REPO_PATH"] = _fields["DEV_REPO_PATH"].Text.Trim(),
            ["AZURE_DEVOPS_PAT"] = _fields["AZURE_DEVOPS_PAT"].Text.Trim(),
            ["TEAMS_WEBHOOK_URL"] = _fields["TEAMS_WEBHOOK_URL"].Text.Trim(),
            ["TEAMS_POST_WORKFLOW_URL"] = _fields["TEAMS_POST_WORKFLOW_URL"].Text.Trim(),
        };
        SaveRequested?.Invoke(values); // last: the host may dispose this panel in response
    }

    private void Fail(string message)
    {
        _statusIsError = true;
        _status.ForeColor = ErrorColor;
        _status.Text = message;
    }

    private void ShowInfo(string message)
    {
        _statusIsError = false;
        _status.ForeColor = Theme.TextSecondary;
        _status.Text = message;
    }

    // Toggle the buttons/inputs while a network verification is in flight.
    private void SetBusy(bool busy, string status)
    {
        IsBusy = busy;
        _save.Enabled = !busy;
        foreach (var box in _fields.Values) box.Enabled = !busy;
        UseWaitCursor = busy;
        ShowInfo(status);
    }
}
