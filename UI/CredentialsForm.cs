using System.Drawing;

namespace NoisLogTray;

// First-run / invalid-config dialog: a borderless rounded card around a SettingsPanel. No Quit
// here on purpose: next to Settings it reads as "close this dialog".
internal sealed class CredentialsForm : CardDialog
{
    private static readonly Font TitleFont = UiScale.Font("Segoe UI Semibold", 13F, FontStyle.Bold);

    private static readonly int ClientW = SettingsPanel.ClientW;
    private static readonly int Pad = UiScale.S(20);
    private static readonly int HeaderH = UiScale.S(58);

    private readonly Label _title = new();
    private readonly CloseButton _close = new() { Surface = () => Theme.CardSurface };
    private readonly SettingsPanel _panel;
    private readonly bool _firstRun;

    internal IReadOnlyDictionary<string, string> Values { get; private set; } =
        new Dictionary<string, string>();

    internal CredentialsForm(IReadOnlyDictionary<string, string> initial, bool firstRun)
    {
        _firstRun = firstRun;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = firstRun; // first run has no window behind it, so keep it findable

        _title.AutoSize = true;
        _title.Location = new Point(Pad, UiScale.S(18));
        _title.Font = TitleFont;
        _title.ForeColor = Theme.TextPrimary;
        _title.BackColor = Color.Transparent;
        Controls.Add(_title);

        _close.Size = UiScale.S(new Size(26, 26));
        _close.Location = new Point(ClientW - Pad - UiScale.S(26) + UiScale.S(4), UiScale.S(19));
        _close.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(_close);

        _panel = new SettingsPanel(initial) { Location = new Point(0, HeaderH) };
        _panel.MouseDown += (_, e) => OnMouseDown(e); // the panel background drags the window too
        _panel.SaveRequested += values =>
        {
            Values = values;
            DialogResult = DialogResult.OK;
            Close();
        };
        Controls.Add(_panel);

        ClientSize = new Size(ClientW, HeaderH + _panel.Height);
        CancelButton = _close;

        ApplyLanguage();
        Lang.Changed += ApplyLanguage;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Lang.Changed -= ApplyLanguage;
        base.Dispose(disposing);
    }

    private void ApplyLanguage()
    {
        Text = _firstRun ? Lang.T("Set up NOIS Workday", "Thiết lập NOIS Workday") : Lang.T("Settings", "Cài đặt");
        _title.Text = Text;
        _close.AccessibleName = Lang.T("Close", "Đóng");
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _panel.FocusFirstField(); // a caret, not a focus ring on the close button
    }
}
