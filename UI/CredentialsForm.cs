using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace NoisLogTray;

// First-run / invalid-config dialog: a borderless rounded card around a SettingsPanel. No Quit
// here on purpose: next to Settings it reads as "close this dialog".
internal sealed class CredentialsForm : Form
{
    private static readonly Font TitleFont = UiScale.Font("Segoe UI Semibold", 13F, FontStyle.Bold);

    private static readonly int ClientW = SettingsPanel.ClientW;
    private static readonly int Pad = UiScale.S(20);
    private static readonly int HeaderH = UiScale.S(58);
    private static readonly int Radius = UiScale.S(12);

    private readonly Label _title = new();
    private readonly CloseButton _close = new() { Surface = () => Theme.CardSurface };
    private readonly SettingsPanel _panel;
    private readonly bool _firstRun;

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
        _panel.FocusFirstField(); // a caret, not a focus ring on the close button
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
