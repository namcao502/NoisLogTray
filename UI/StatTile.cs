using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace NoisLogTray;

// One Dashboard figure on a small card: a title, a colored dot and value, and a detail line under it. Placed by the page's
// LayoutCards, so its labels follow its width here rather than through Anchor.
internal sealed class StatTile : Card
{
    private static readonly Font TitleFont = UiScale.Font("Segoe UI", 8.5F, FontStyle.Bold);
    private static readonly Font ValueFont = UiScale.Font("Segoe UI Semibold", 11F);
    private static readonly Font DetailFont = UiScale.Font("Segoe UI", 9F);
    private static readonly int Pad = UiScale.S(14);
    private static readonly int DotSize = UiScale.S(10);
    private static readonly int ValueX = UiScale.S(30); // after the dot
    private static readonly int TitleY = UiScale.S(10);
    private static readonly int ValueY = UiScale.S(29);
    private static readonly int DetailY = UiScale.S(53);

    private readonly Label _title = new();
    private readonly Label _value = new();
    private readonly Label _detail = new();

    internal Color Tone { get; private set; } = Theme.TextSecondary;

    internal string Value => _value.Text;

    internal string Detail => _detail.Text;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string Title
    {
        get => _title.Text;
        set
        {
            _title.Text = value;
            UpdateAccessibleName();
        }
    }

    internal StatTile()
    {
        AccessibleRole = AccessibleRole.StaticText;
        foreach (var (label, font) in new[] { (_title, TitleFont), (_value, ValueFont), (_detail, DetailFont) })
        {
            label.AutoSize = false;
            label.Font = font;
            label.BackColor = Color.Transparent;
            label.UseMnemonic = false;
            label.TextAlign = ContentAlignment.MiddleLeft;
            ThemedToolTip.Track(label); // cut with "...", the full text in a tooltip
            Controls.Add(label);
        }
        ApplyTheme();
        Theme.Changed += ApplyTheme;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= ApplyTheme;
        base.Dispose(disposing);
    }

    internal void SetValue(Color tone, string value, string detail)
    {
        Tone = tone;
        _value.Text = value;
        _value.ForeColor = tone;
        _detail.Text = detail;
        UpdateAccessibleName();
        Invalidate(); // the dot
    }

    private void UpdateAccessibleName() =>
        AccessibleName = Detail.Length == 0 ? $"{Title}: {Value}" : $"{Title}: {Value}. {Detail}";

    private void ApplyTheme()
    {
        _title.ForeColor = Theme.TextSecondary;
        _detail.ForeColor = Theme.TextSecondary;
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        var textWidth = Math.Max(0, Width - 2 * Pad);
        _title.SetBounds(Pad, TitleY, textWidth, TitleFont.Height + UiScale.S(2));
        _value.SetBounds(ValueX, ValueY, Math.Max(0, Width - Pad - ValueX), ValueFont.Height + UiScale.S(2));
        _detail.SetBounds(Pad, DetailY, textWidth, DetailFont.Height + UiScale.S(2));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var dotBrush = new SolidBrush(Tone);
        e.Graphics.FillEllipse(dotBrush, Pad, _value.Top + (_value.Height - DotSize) / 2, DotSize, DotSize);
    }
}
