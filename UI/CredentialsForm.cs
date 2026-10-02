using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;

namespace NoisLogTray;

// First-run setup / Settings dialog, a borderless rounded card. The top options (language,
// start with Windows) apply as soon as a pill is clicked; the account fields (Jira site +
// account, HRM key, TSC columns, daily log time) are verified and returned on Save as
// key/value pairs for AppConfig.SaveUserConfig. The footer opens the log folder. There is
// deliberately no Quit here: next to Settings it reads as "close this dialog". Secrets are masked.
internal sealed class CredentialsForm : Form
{
    private static readonly Font TitleFont = new("Segoe UI Semibold", 13F, FontStyle.Bold);
    private static readonly Font LabelFont = new("Segoe UI", 9F);
    private static readonly Font HelpFont = new("Segoe UI", 8.5F);
    private static readonly Font InputFont = new("Segoe UI", 9.5F);
    private static readonly Font PillFont = new("Segoe UI", 9.5F);

    private const int ClientW = 400;
    private const int Pad = 20;
    private const int InnerW = ClientW - 2 * Pad;
    private const int RowH = 30; // pills, inputs and buttons share one height, as in the main window
    private const int Gap = 8;
    private const int Radius = 12;

    private readonly Dictionary<string, TextBox> _fields = new();
    private readonly Label _status = new();
    private readonly List<(Control Control, string English, string Vietnamese)> _texts = new();
    private readonly MacButton _english = MacButton.Secondary("English");
    private readonly MacButton _vietnamese = MacButton.Secondary("Tiếng Việt");
    private readonly MacButton _startupOn = MacButton.Secondary("");
    private readonly MacButton _startupOff = MacButton.Secondary("");
    private readonly MacButton _save = MacButton.Primary("");
    private readonly CloseButton _close = new() { Surface = () => Theme.CardSurface };
    private readonly bool _firstRun;
    private int _y = 18;

    internal IReadOnlyDictionary<string, string> Values { get; private set; } =
        new Dictionary<string, string>();

    internal CredentialsForm(IReadOnlyDictionary<string, string> initial, bool firstRun)
    {
        _firstRun = firstRun;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = firstRun; // first run has no window behind it, so keep it findable
        BackColor = Theme.CardSurface;
        Icon = AppIcon.Load(32);

        AddHeader();
        AddOptionGroup("Language", "Ngôn ngữ", _vietnamese, _english);
        AddOptionGroup("Start with Windows", "Khởi động cùng Windows", _startupOn, _startupOff);
        _english.Click += (_, _) => SetLanguage(vietnamese: false);
        _vietnamese.Click += (_, _) => SetLanguage(vietnamese: true);
        _startupOn.Click += (_, _) => SetStartup(true);
        _startupOff.Click += (_, _) => SetStartup(false);
        AddDivider();

        AddLabel("Account - applies when you press Save", "Tài khoản - áp dụng khi bấm Lưu", LabelFont, Theme.TextPrimary);
        AddHelp();
        AddField("Jira site URL", "Địa chỉ Jira", "JIRA_BASE_URL", secret: false,
            Value(initial, "JIRA_BASE_URL", AppConfig.DefaultJiraBaseUrl));
        AddField("Jira email", "Email Jira", "JIRA_EMAIL", secret: false, Value(initial, "JIRA_EMAIL", ""));
        // Short values share a row to keep the card compact.
        AddFieldPair(
            ("Jira API token", "API token Jira", "JIRA_API_TOKEN", true, Value(initial, "JIRA_API_TOKEN", "")),
            ("HRM API key", "API key HRM", "HRM_API_KEY", true, Value(initial, "HRM_API_KEY", "")));
        AddFieldPair(
            ("TSC columns (e.g. M, J)", "Cột TSC (vd. M, J)", "TSC_GRAPH_COLUMNS", false,
                Value(initial, "TSC_GRAPH_COLUMNS", "M, J")),
            ("Daily log time (e.g. 6:00 PM)", "Giờ tự log (vd. 6:00 PM)", "LOG_TIME", false,
                AppConfig.ParseLogTime(Value(initial, "LOG_TIME", "")).ToString("h:mm tt", CultureInfo.InvariantCulture)));
        AddStatusAndSave();
        AddDivider();
        AddFooter();

        ClientSize = new Size(ClientW, _y + Pad);
        AcceptButton = _save;
        CancelButton = _close;

        ApplyLanguage();
        ShowSelections();
        Lang.Changed += ApplyLanguage;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Lang.Changed -= ApplyLanguage;
        base.Dispose(disposing);
    }

    private static string Value(IReadOnlyDictionary<string, string> initial, string key, string fallback) =>
        initial.TryGetValue(key, out var v) && v.Length != 0 ? v : fallback;

    private void AddHeader()
    {
        var title = new Label
        {
            AutoSize = true,
            Location = new Point(Pad, _y),
            Font = TitleFont,
            ForeColor = Theme.TextPrimary,
            BackColor = Color.Transparent,
        };
        Controls.Add(title);
        if (_firstRun) _texts.Add((title, "Set up NOIS Daily Log", "Thiết lập NOIS Daily Log"));
        else _texts.Add((title, "Settings", "Cài đặt"));

        _close.Size = new Size(26, 26);
        _close.Location = new Point(ClientW - Pad - 26 + 4, _y + 1);
        _close.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(_close);
        _y += 40;
    }

    // A muted caption over a row of pills; the selected pill reads as the primary button.
    private void AddOptionGroup(string english, string vietnamese, params MacButton[] pills)
    {
        AddLabel(english, vietnamese, LabelFont, Theme.TextSecondary);
        var x = Pad;
        foreach (var pill in pills)
        {
            pill.Font = PillFont;
            pill.Location = new Point(x, _y);
            pill.Size = new Size(PillWidth(pill), RowH);
            Controls.Add(pill);
            x += pill.Width + Gap;
        }
        _y += RowH + 14;
    }

    // Wide enough for the pill's text in either language.
    private static int PillWidth(MacButton pill)
    {
        if (pill.Text.Length != 0) return TextRenderer.MeasureText(pill.Text, PillFont).Width + 28;
        return 64; // On/Off pills: "On"/"Bật", "Off"/"Tắt"
    }

    private void AddLabel(string english, string vietnamese, Font font, Color color)
    {
        var label = new Label
        {
            AutoSize = true,
            Location = new Point(Pad, _y),
            Font = font,
            ForeColor = color,
            BackColor = Color.Transparent,
        };
        Controls.Add(label);
        _texts.Add((label, english, vietnamese));
        _y += 20;
    }

    private void AddHelp()
    {
        var help = new Label
        {
            AutoSize = false,
            Size = new Size(InnerW, 18),
            Location = new Point(Pad, _y),
            Font = HelpFont,
            ForeColor = Theme.TextSecondary,
            BackColor = Color.Transparent,
        };
        Controls.Add(help);
        _texts.Add((help,
            "Stored only on this PC. Get a Jira token at id.atlassian.com.",
            "Chỉ lưu trên máy này. Lấy Jira token tại id.atlassian.com."));
        _y += 24;
    }

    private void AddField(string english, string vietnamese, string key, bool secret, string value)
    {
        AddFieldAt(Pad, InnerW, english, vietnamese, key, secret, value);
        _y += 18 + RowH + 10;
    }

    private void AddFieldPair(
        (string English, string Vietnamese, string Key, bool Secret, string Value) left,
        (string English, string Vietnamese, string Key, bool Secret, string Value) right)
    {
        var width = (InnerW - Gap) / 2;
        AddFieldAt(Pad, width, left.English, left.Vietnamese, left.Key, left.Secret, left.Value);
        AddFieldAt(Pad + width + Gap, width, right.English, right.Vietnamese, right.Key, right.Secret, right.Value);
        _y += 18 + RowH + 10;
    }

    // A muted caption with a rounded input under it, at the current row.
    private void AddFieldAt(int x, int width, string english, string vietnamese, string key, bool secret, string value)
    {
        var caption = new Label
        {
            AutoSize = true,
            Location = new Point(x, _y),
            Font = HelpFont,
            ForeColor = Theme.TextSecondary,
            BackColor = Color.Transparent,
        };
        Controls.Add(caption);
        _texts.Add((caption, english, vietnamese));

        var host = new RoundedHost { Location = new Point(x, _y + 18), Size = new Size(width, RowH) };
        var box = new TextBox
        {
            BorderStyle = BorderStyle.None,
            Font = InputFont,
            BackColor = Theme.InputBg,
            ForeColor = Theme.TextPrimary,
            UseSystemPasswordChar = secret,
            Text = value,
        };
        var h = box.PreferredHeight;
        box.SetBounds(10, (host.Height - h) / 2, host.Width - 20, h);
        host.Controls.Add(box);
        Controls.Add(host);
        _fields[key] = box;
    }

    private void AddStatusAndSave()
    {
        const int saveW = 100;
        _status.AutoSize = false;
        _status.Size = new Size(InnerW - saveW - Gap, RowH);
        _status.Location = new Point(Pad, _y);
        _status.Font = HelpFont;
        _status.BackColor = Color.Transparent;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        Controls.Add(_status);

        _save.Size = new Size(saveW, RowH);
        _save.Location = new Point(Pad + InnerW - saveW, _y);
        _save.Click += OnSave;
        Controls.Add(_save);
        _texts.Add((_save, "Save", "Lưu"));
        _y += RowH + 14;
    }

    private void AddDivider()
    {
        Controls.Add(new Panel { Location = new Point(Pad, _y), Size = new Size(InnerW, 1), BackColor = Theme.CardBorder });
        _y += 14;
    }

    private void AddFooter()
    {
        var logs = MacButton.Secondary("");
        logs.Size = new Size(Math.Max(TextRenderer.MeasureText("Open log folder", PillFont).Width,
            TextRenderer.MeasureText("Mở thư mục log", PillFont).Width) + 28, RowH);
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

    private void ApplyLanguage()
    {
        Text = _firstRun ? Lang.T("Set up NOIS Daily Log", "Thiết lập NOIS Daily Log") : Lang.T("Settings", "Cài đặt");
        foreach (var (control, english, vietnamese) in _texts) control.Text = Lang.T(english, vietnamese);
        _startupOn.Text = Lang.T("On", "Bật");
        _startupOff.Text = Lang.T("Off", "Tắt");
        _close.AccessibleName = Lang.T("Close", "Đóng");
    }

    private void ShowSelections()
    {
        _english.Selected = !Lang.Vietnamese;
        _vietnamese.Selected = Lang.Vietnamese;
        var startup = StartupService.IsEnabled();
        _startupOn.Selected = startup;
        _startupOff.Selected = !startup;
    }

    private void SetLanguage(bool vietnamese)
    {
        if (Lang.Vietnamese != vietnamese) Lang.Toggle(); // re-labels this dialog and the app
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

    private async void OnSave(object? sender, EventArgs e)
    {
        var baseUrl = _fields["JIRA_BASE_URL"].Text.Trim();
        var email = _fields["JIRA_EMAIL"].Text.Trim();
        var token = _fields["JIRA_API_TOKEN"].Text.Trim();
        var hrmKey = _fields["HRM_API_KEY"].Text.Trim();
        var columns = TscCells.ParseColumns(_fields["TSC_GRAPH_COLUMNS"].Text);
        var logTimeText = _fields["LOG_TIME"].Text.Trim();

        // Format checks first (cheap, offline).
        if (!baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            Fail(Lang.T("Jira site URL must start with http.", "Địa chỉ Jira phải bắt đầu bằng http."));
            return;
        }
        if (!email.Contains('@'))
        {
            Fail(Lang.T("Enter a valid Jira email.", "Hãy nhập email Jira hợp lệ."));
            return;
        }
        if (token.Length == 0 || hrmKey.Length == 0)
        {
            Fail(Lang.T("Jira API token and HRM API key are required.", "Cần nhập API token Jira và API key HRM."));
            return;
        }
        if (columns.Count == 0)
        {
            Fail(Lang.T("Enter at least one TSC column (e.g. M, J).", "Hãy nhập ít nhất một cột TSC (vd. M, J)."));
            return;
        }
        if (!TimeOnly.TryParseExact(logTimeText, AppConfig.LogTimeFormats,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var logTime))
        {
            Fail(Lang.T("Enter the daily log time in 12-hour format, e.g. 6:00 PM.", "Hãy nhập giờ tự log theo dạng 12 giờ, vd. 6:00 PM."));
            return;
        }

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
            SetBusy(false, "");
        }

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
            var answer = MessageBox.Show(this,
                Lang.T($"Could not reach {which} to verify your details (are you online?).\n\nSave anyway?",
                    $"Không kết nối được {which} để kiểm tra thông tin (bạn có đang online không?).\n\nVẫn lưu?"),
                Lang.T("Couldn't verify", "Không kiểm tra được"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes) return;
        }

        Values = new Dictionary<string, string>
        {
            ["JIRA_BASE_URL"] = baseUrl,
            ["JIRA_EMAIL"] = email,
            ["JIRA_API_TOKEN"] = token,
            ["HRM_API_KEY"] = hrmKey,
            ["TSC_GRAPH_COLUMNS"] = string.Join(", ", columns),
            ["LOG_TIME"] = logTime.ToString("h:mm tt", CultureInfo.InvariantCulture),
        };
        DialogResult = DialogResult.OK;
        Close();
    }

    private void Fail(string message)
    {
        _status.ForeColor = Color.FromArgb(230, 76, 76);
        _status.Text = message;
    }

    private void ShowInfo(string message)
    {
        _status.ForeColor = Theme.TextSecondary;
        _status.Text = message;
    }

    // Toggle the buttons/inputs while a network verification is in flight.
    private void SetBusy(bool busy, string status)
    {
        _save.Enabled = !busy;
        foreach (var box in _fields.Values) box.Enabled = !busy;
        UseWaitCursor = busy;
        ShowInfo(status);
    }

    // ---- Borderless card chrome: shadow, rounded corners, border, drag to move.

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ActiveControl = _fields["JIRA_EMAIL"]; // a caret, not a focus ring on the close button
        using var path = Rounded(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), Radius);
        Region = new Region(path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Rounded(new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1), Radius);
        using var pen = new Pen(Theme.CardBorder, 1f);
        e.Graphics.DrawPath(pen, path);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    // There is no title bar, so dragging the card's background moves the window.
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
