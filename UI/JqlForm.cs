using System.Drawing;

namespace NoisLogTray;

// Small themed dialog to edit the custom "My tickets" JQL. Pre-filled with the current
// effective query; validates against Jira on Save (via the injected validator) so a
// broken query is never stored. Returns the entered query in Jql (empty == use default).
internal sealed class JqlForm : Form
{
    private static readonly Font TitleFont = new("Segoe UI Semibold", 13F, FontStyle.Bold);
    private static readonly Font HelpFont = new("Segoe UI", 8.5F);
    private static readonly Font CaptionFont = new("Segoe UI", 8.5F, FontStyle.Bold);
    private static readonly Font InputFont = new("Segoe UI", 9.5F);

    private const int Pad = 24;
    private const int FieldW = 472; // ClientWidth (520) - 2*Pad

    private readonly TextBox _box = new();
    private readonly Label _error = new();
    private readonly string _defaultJql;
    private readonly Func<string, CancellationToken, Task<JqlCheckResult>> _validate;
    private MacButton _save = null!;
    private MacButton _cancel = null!;
    private MacButton _reset = null!;

    internal string Jql { get; private set; } = "";

    internal JqlForm(string initialJql, string defaultJql,
        Func<string, CancellationToken, Task<JqlCheckResult>> validate)
    {
        _defaultJql = defaultJql;
        _validate = validate;

        Text = "Edit ticket query";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.WindowBg;
        Icon = AppIcon.Load(32);

        var y = Pad;
        y = AddHeader(y);
        y = AddField(y, initialJql);
        AddErrorAndButtons(y);
    }

    private int AddHeader(int y)
    {
        var title = new Label
        {
            Text = "Ticket query (JQL)",
            AutoSize = true,
            Location = new Point(Pad, y),
            Font = TitleFont,
            ForeColor = Theme.TextPrimary,
            BackColor = Color.Transparent,
        };
        Controls.Add(title);
        y += 28;

        var help = new Label
        {
            Text = "This JQL drives the \"My tickets\" list. Leave blank to use the built-in default.\n" +
                   "Checked against Jira when you save.",
            AutoSize = false,
            Size = new Size(FieldW, 34),
            Location = new Point(Pad, y),
            Font = HelpFont,
            ForeColor = Theme.TextSecondary,
            BackColor = Color.Transparent,
        };
        Controls.Add(help);
        return y + 42;
    }

    private int AddField(int y, string initialJql)
    {
        var cap = new Label
        {
            Text = "JQL",
            AutoSize = true,
            Location = new Point(Pad, y),
            Font = CaptionFont,
            ForeColor = Theme.TextSecondary,
            BackColor = Color.Transparent,
        };
        Controls.Add(cap);
        y += 20;

        var host = new RoundedHost { Location = new Point(Pad, y), Size = new Size(FieldW, 110) };
        _box.Multiline = true;
        _box.AcceptsReturn = true; // Enter inserts a newline rather than triggering Save
        _box.ScrollBars = ScrollBars.Vertical;
        _box.BorderStyle = BorderStyle.None;
        _box.Font = InputFont;
        _box.BackColor = Theme.InputBg;
        _box.ForeColor = Theme.TextPrimary;
        _box.Text = initialJql;
        _box.SetBounds(10, 8, host.Width - 20, host.Height - 16);
        host.Controls.Add(_box);
        Controls.Add(host);
        return y + 110 + 12;
    }

    private void AddErrorAndButtons(int y)
    {
        _error.AutoSize = false;
        _error.Size = new Size(FieldW, 18);
        _error.Location = new Point(Pad, y);
        _error.Font = HelpFont;
        _error.ForeColor = Color.FromArgb(230, 76, 76);
        _error.BackColor = Color.Transparent;
        _error.TextAlign = ContentAlignment.MiddleLeft;
        Controls.Add(_error);
        y += 24;

        _reset = MacButton.Secondary("Reset to default");
        _reset.OnWindow = true;
        _reset.Size = new Size(130, 36);
        _reset.Location = new Point(Pad, y);
        _reset.Click += (_, _) => { _box.Text = _defaultJql; _box.Focus(); };

        _save = MacButton.Primary("Save");
        _save.OnWindow = true;
        _save.Size = new Size(110, 36);
        _save.Location = new Point(Pad + FieldW - 110, y);
        _save.Click += OnSave;

        _cancel = MacButton.Secondary("Cancel");
        _cancel.OnWindow = true;
        _cancel.Size = new Size(100, 36);
        _cancel.Location = new Point(Pad + FieldW - 110 - 12 - 100, y);
        _cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        Controls.Add(_reset);
        Controls.Add(_save);
        Controls.Add(_cancel);
        y += 36 + Pad;

        CancelButton = _cancel; // Esc closes; Enter is left to the multiline box
        ClientSize = new Size(520, y);
    }

    private async void OnSave(object? sender, EventArgs e)
    {
        var text = _box.Text.Trim();

        // Empty means "use the built-in default" - no query to validate.
        if (text.Length == 0)
        {
            Jql = "";
            DialogResult = DialogResult.OK;
            Close();
            return;
        }

        JqlCheckResult result;
        SetBusy(true, "Checking query...");
        try
        {
            result = await _validate(text, CancellationToken.None);
        }
        finally
        {
            SetBusy(false, "");
        }

        switch (result.Status)
        {
            case JqlCheck.Valid:
                Jql = text;
                DialogResult = DialogResult.OK;
                Close();
                return;

            case JqlCheck.Invalid:
                Fail(result.Error ?? "Jira rejected this query.");
                return;

            case JqlCheck.Unreachable:
                var answer = MessageBox.Show(this,
                    "Couldn't reach Jira to check this query (are you online?).\n\nSave anyway?",
                    "Couldn't verify", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer == DialogResult.Yes)
                {
                    Jql = text;
                    DialogResult = DialogResult.OK;
                    Close();
                }
                return;
        }
    }

    private void Fail(string message)
    {
        _error.ForeColor = Color.FromArgb(230, 76, 76);
        _error.Text = message;
    }

    // Toggle the buttons/input while the Jira check is in flight.
    private void SetBusy(bool busy, string status)
    {
        _save.Enabled = !busy;
        _cancel.Enabled = !busy;
        _reset.Enabled = !busy;
        _box.Enabled = !busy;
        UseWaitCursor = busy;
        _error.ForeColor = Theme.TextSecondary;
        _error.Text = status;
    }
}
