using System.Drawing;
using System.Drawing.Drawing2D;

namespace NoisLogTray;

// The header's activity sign, left of the bell: a turning arc while BusyWork has anything running, its tooltip naming
// the work. Idle it is drawn empty but keeps its place, so the header controls beside it never shift.
internal sealed class BusySpinner : Control
{
    // Work quicker than this never shows the spinner, so a fast check does not flicker it.
    private const int ShowDelayMs = 250;
    private const int DegreesPerTick = 12;

    private readonly BusyWork _work;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 30 };
    private readonly ThemedToolTip _tip = new();
    private long _busySince; // Environment.TickCount64 when the work began; 0 while idle
    private int _angle;

    internal BusySpinner(BusyWork work)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        AccessibleRole = AccessibleRole.Animation;
        AccessibleName = "Idle";
        _work = work;
        _timer.Tick += (_, _) =>
        {
            _angle = (_angle + DegreesPerTick) % 360;
            Invalidate();
        };
        _work.Changed += OnWorkChanged;
        Theme.Changed += Invalidate;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _work.Changed -= OnWorkChanged;
            Theme.Changed -= Invalidate;
            _timer.Dispose();
            _tip.Dispose();
        }
        base.Dispose(disposing);
    }

    // Work that began before the window was built shows at once.
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Sync();
    }

    // Raised on whichever thread began or ended the work.
    private void OnWorkChanged()
    {
        if (!IsHandleCreated) return; // OnHandleCreated syncs
        try
        {
            BeginInvoke(Sync);
        }
        catch (InvalidOperationException)
        {
            // The handle went away in between (the shell was closed); a new spinner syncs on its own.
        }
    }

    private void Sync()
    {
        if (IsDisposed) return;
        var labels = _work.Labels;
        if (labels.Count == 0)
        {
            _busySince = 0;
            _timer.Stop();
        }
        else if (_busySince == 0)
        {
            _busySince = Environment.TickCount64;
            _timer.Start();
        }
        var text = labels.Count == 0 ? null : string.Join(Environment.NewLine, labels);
        _tip.SetToolTip(this, text);
        AccessibleName = text ?? "Idle";
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.WindowBg);
        if (_busySince == 0 || Environment.TickCount64 - _busySince < ShowDelayMs) return;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        var diameter = UiScale.S(18f);
        var circle = new RectangleF((Width - diameter) / 2f, (Height - diameter) / 2f, diameter, diameter);
        using var track = new Pen(Theme.Divider, UiScale.S(2.5f));
        g.DrawEllipse(track, circle);
        using var arc = new Pen(Theme.Accent, UiScale.S(2.5f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(arc, circle, _angle, 100);
    }
}
