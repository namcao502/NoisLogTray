using System.Drawing;

namespace NoisLogTray;

// First-run / edit-credentials dialog. Collects the per-user config (Jira site +
// account, HRM key, and the user's TSC columns) and returns it as key/value pairs
// for AppConfig.SaveUserEnv. Themed to match the app; secrets are masked.
internal sealed class CredentialsForm : Form
{
    private static readonly Font TitleFont = new("Segoe UI Semibold", 13F, FontStyle.Bold);
    private static readonly Font HelpFont = new("Segoe UI", 8.5F);
    private static readonly Font CaptionFont = new("Segoe UI", 8.5F, FontStyle.Bold);
    private static readonly Font InputFont = new("Segoe UI", 9.5F);

    private const int Pad = 24;
    private const int FieldW = 412; // ClientWidth (460) - 2*Pad

    private readonly Dictionary<string, TextBox> _fields = new();
    private readonly Label _error = new();
    private int _y = Pad;

    internal IReadOnlyDictionary<string, string> Values { get; private set; } =
        new Dictionary<string, string>();

    internal CredentialsForm(IReadOnlyDictionary<string, string> initial, bool firstRun)
    {
        Text = firstRun ? "Set up NOIS Daily Log" : "Edit credentials";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.WindowBg;
        Icon = AppIcon.Load(32);
        ClientSize = new Size(460, 100); // height finalized after fields are added

        AddTitle(firstRun);
        AddField("JIRA SITE URL", "JIRA_BASE_URL", secret: false,
            Value(initial, "JIRA_BASE_URL", AppConfig.DefaultJiraBaseUrl));
        AddField("JIRA EMAIL", "JIRA_EMAIL", secret: false, Value(initial, "JIRA_EMAIL", ""));
        AddField("JIRA API TOKEN", "JIRA_API_TOKEN", secret: true, Value(initial, "JIRA_API_TOKEN", ""));
        AddField("HRM API KEY", "HRM_API_KEY", secret: true, Value(initial, "HRM_API_KEY", ""));
        AddField("TSC COLUMNS  (comma-separated, e.g. M, J)", "TSC_GRAPH_COLUMNS", secret: false,
            Value(initial, "TSC_GRAPH_COLUMNS", "M, J"));

        AddErrorLabelAndButtons();
    }

    private static string Value(IReadOnlyDictionary<string, string> initial, string key, string fallback) =>
        initial.TryGetValue(key, out var v) && v.Length != 0 ? v : fallback;

    private void AddTitle(bool firstRun)
    {
        var title = new Label
        {
            Text = firstRun ? "Welcome - enter your details" : "Edit your details",
            AutoSize = true,
            Location = new Point(Pad, _y),
            Font = TitleFont,
            ForeColor = Theme.TextPrimary,
            BackColor = Color.Transparent,
        };
        Controls.Add(title);
        _y += 28;

        var help = new Label
        {
            Text = "Stored only on this PC. Get a Jira token at id.atlassian.com.\n" +
                   "After saving, use \"Re-authenticate TSC\" to sign in to Microsoft.",
            AutoSize = false,
            Size = new Size(FieldW, 34),
            Location = new Point(Pad, _y),
            Font = HelpFont,
            ForeColor = Theme.TextSecondary,
            BackColor = Color.Transparent,
        };
        Controls.Add(help);
        _y += 42;
    }

    private void AddField(string caption, string key, bool secret, string value)
    {
        var cap = new Label
        {
            Text = caption,
            AutoSize = true,
            Location = new Point(Pad, _y),
            Font = CaptionFont,
            ForeColor = Theme.TextSecondary,
            BackColor = Color.Transparent,
        };
        Controls.Add(cap);
        _y += 20;

        var host = new RoundedHost { Location = new Point(Pad, _y), Size = new Size(FieldW, 34) };
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
        _y += 34 + 12;
    }

    private void AddErrorLabelAndButtons()
    {
        _error.AutoSize = false;
        _error.Size = new Size(FieldW, 18);
        _error.Location = new Point(Pad, _y);
        _error.Font = HelpFont;
        _error.ForeColor = Color.FromArgb(230, 76, 76);
        _error.BackColor = Color.Transparent;
        _error.TextAlign = ContentAlignment.MiddleLeft;
        Controls.Add(_error);
        _y += 24;

        var save = MacButton.Primary("Save");
        save.OnWindow = true;
        save.Size = new Size(120, 36);
        save.Location = new Point(Pad + FieldW - 120, _y);
        save.Click += OnSave;

        var cancel = MacButton.Secondary("Cancel");
        cancel.OnWindow = true;
        cancel.Size = new Size(100, 36);
        cancel.Location = new Point(Pad + FieldW - 120 - 12 - 100, _y);
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        Controls.Add(save);
        Controls.Add(cancel);
        _y += 36 + Pad;

        AcceptButton = save;
        CancelButton = cancel;
        ClientSize = new Size(460, _y);
    }

    private void OnSave(object? sender, EventArgs e)
    {
        var baseUrl = _fields["JIRA_BASE_URL"].Text.Trim();
        var email = _fields["JIRA_EMAIL"].Text.Trim();
        var token = _fields["JIRA_API_TOKEN"].Text.Trim();
        var hrmKey = _fields["HRM_API_KEY"].Text.Trim();
        var columns = TscCells.ParseColumns(_fields["TSC_GRAPH_COLUMNS"].Text);

        if (!baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            Fail("Jira site URL must start with http.");
            return;
        }
        if (!email.Contains('@'))
        {
            Fail("Enter a valid Jira email.");
            return;
        }
        if (token.Length == 0 || hrmKey.Length == 0)
        {
            Fail("Jira API token and HRM API key are required.");
            return;
        }
        if (columns.Count == 0)
        {
            Fail("Enter at least one TSC column (e.g. M, J).");
            return;
        }

        Values = new Dictionary<string, string>
        {
            ["JIRA_BASE_URL"] = baseUrl,
            ["JIRA_EMAIL"] = email,
            ["JIRA_API_TOKEN"] = token,
            ["HRM_API_KEY"] = hrmKey,
            ["TSC_GRAPH_COLUMNS"] = string.Join(", ", columns),
        };
        DialogResult = DialogResult.OK;
        Close();
    }

    private void Fail(string message) => _error.Text = message;
}
