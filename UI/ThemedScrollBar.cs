using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace NoisLogTray;

// The app's scrollbar instead of Windows' own: a thin rounded thumb in the secondary text color, laid over the strip
// where the target's native bar sits. The target keeps all of its own scrolling (wheel, keys, AutoScroll layout,
// ScrollControlIntoView); a window Region clips its native bar away and this sibling draws and drives the scroll.
// It follows the target on every Application.Idle, so nothing at the call site has to tell it about changes.
internal sealed class ThemedScrollBar : Control
{
    private const int GwlStyle = -16;
    private const int WsVScroll = 0x00200000;
    private const int WsHScroll = 0x00100000;
    private const int SbHorz = 0;
    private const int SbVert = 1;
    private const int SifRange = 0x1;
    private const int SifPage = 0x2;
    private const int SifPos = 0x4;
    private const int WmMouseWheel = 0x020A;
    private const int WmMouseHWheel = 0x020E;
    private const int EmLineScroll = 0x00B6;

    // The app's one scrollbar look, also in the web views' CSS and the terminal's: a pill 8px thick (11px under the
    // mouse or while dragged), 3px in from the outer edge and the track ends, at 40% / 60% / 80% of TextSecondary.
    private static readonly int TrackPad = UiScale.S(2);
    private static readonly int EdgeInset = UiScale.S(2);
    private static readonly int ThumbThickness = UiScale.S(6);
    private static readonly int ThumbHoverThickness = UiScale.S(9);
    private static readonly int MinThumbLength = UiScale.S(24);

    private readonly Control _target;
    private readonly bool _vertical;
    private ScrollState _state;
    private Rectangle? _clip;
    private bool _hover;
    private int? _dragOffset; // the pointer's offset into the thumb while it is dragged

    // NativeVisible: the target shows a native bar (a multiline TextBox shows one, disabled, even when its text fits).
    private readonly record struct ScrollState(bool NativeVisible, int Minimum, int Maximum, int Page, int Position);

    private ThemedScrollBar(Control target, bool vertical)
    {
        _target = target;
        _vertical = vertical;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        Visible = false;
        Application.Idle += OnIdle;
        Theme.Changed += Invalidate;
        target.VisibleChanged += OnTargetChanged;
        target.Disposed += (_, _) => Dispose();
    }

    // A ScrollableControl with AutoScroll, or a multiline TextBox (vertical only).
    internal static ThemedScrollBar Attach(Control target, bool vertical = true) => new(target, vertical);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Application.Idle -= OnIdle;
            Theme.Changed -= Invalidate;
            _target.VisibleChanged -= OnTargetChanged;
        }
        base.Dispose(disposing);
    }

    // The thumb's (start, length) along a track for Win32 scroll info; null when the page holds the whole range.
    internal static (int Start, int Length)? Thumb(int track, int minimum, int maximum, int page, int position, int minLength)
    {
        var range = maximum - minimum + 1;
        if (page <= 0 || page >= range || track <= 0) return null;
        var length = Math.Min(track, Math.Max(minLength, (int)((long)track * page / range)));
        var scrollable = range - page;
        var offset = Math.Clamp(position - minimum, 0, scrollable);
        return ((int)((long)(track - length) * offset / scrollable), length);
    }

    internal static int PositionAt(int track, int thumbLength, int minimum, int maximum, int page, int thumbStart)
    {
        var travel = track - thumbLength;
        var scrollable = maximum - minimum + 1 - page;
        if (travel <= 0 || scrollable <= 0) return minimum;
        return minimum + (int)Math.Round((double)Math.Clamp(thumbStart, 0, travel) * scrollable / travel);
    }

    private void OnIdle(object? sender, EventArgs e) => Sync();

    private void OnTargetChanged(object? sender, EventArgs e) => Sync();

    // Follows the target's scroll state, place and visibility. Runs on every idle, so it only touches what changed.
    internal void Sync()
    {
        if (IsDisposed || _target.IsDisposed) return;
        var parent = _target.Parent;
        var state = ReadState();
        // A layout panel would place this sibling itself, so there the native bar stays.
        var show = state.NativeVisible && _target.Visible && parent != null && parent is not FlowLayoutPanel && parent is not TableLayoutPanel;
        if (!show)
        {
            ClipTarget(null);
            if (Visible) Visible = false;
            return;
        }

        var clientInParent = parent!.RectangleToClient(_target.RectangleToScreen(_target.ClientRectangle));
        var strip = _vertical
            ? new Rectangle(clientInParent.Right, clientInParent.Top, SystemInformation.GetVerticalScrollBarWidthForDpi(_target.DeviceDpi), clientInParent.Height)
            : new Rectangle(clientInParent.Left, clientInParent.Bottom, clientInParent.Width, SystemInformation.GetHorizontalScrollBarHeightForDpi(_target.DeviceDpi));
        var clientInWindow = clientInParent with { X = clientInParent.X - _target.Left, Y = clientInParent.Y - _target.Top };
        ClipTarget(_vertical
            ? new Rectangle(0, 0, clientInWindow.Right, _target.Height)
            : new Rectangle(0, 0, _target.Width, clientInWindow.Bottom));

        if (Parent != parent) parent.Controls.Add(this);
        if (Bounds != strip) Bounds = strip;
        if (parent.Controls.GetChildIndex(this) != 0) BringToFront();
        if (!Visible) Visible = true;
        if (state != _state)
        {
            _state = state;
            Invalidate();
        }
    }

    private void ClipTarget(Rectangle? clip)
    {
        if (_clip == clip) return;
        _clip = clip;
        var oldRegion = _target.Region;
        _target.Region = clip == null ? null : new Region(clip.Value);
        oldRegion?.Dispose();
    }

    private ScrollState ReadState()
    {
        if (!_target.IsHandleCreated) return default;
        var style = GetWindowLong(_target.Handle, GwlStyle);
        if ((style & (_vertical ? WsVScroll : WsHScroll)) == 0) return default;
        var info = new ScrollInfo { Size = (uint)Marshal.SizeOf<ScrollInfo>(), Mask = SifRange | SifPage | SifPos };
        if (!GetScrollInfo(_target.Handle, _vertical ? SbVert : SbHorz, ref info)) return default;
        return new ScrollState(true, info.Minimum, info.Maximum, (int)info.Page, info.Position);
    }

    private int TrackLength => (_vertical ? Height : Width) - 2 * TrackPad;

    private (int Start, int Length)? CurrentThumb() =>
        Thumb(TrackLength, _state.Minimum, _state.Maximum, _state.Page, _state.Position, MinThumbLength);

    private void ScrollTo(int position)
    {
        var lastPosition = Math.Max(_state.Minimum, _state.Maximum - _state.Page + 1);
        var clamped = Math.Clamp(position, _state.Minimum, lastPosition);
        if (_target is ScrollableControl scrollable)
        {
            // AutoScrollPosition reads negative and is set positive.
            var current = scrollable.AutoScrollPosition;
            scrollable.AutoScrollPosition = _vertical ? new Point(-current.X, clamped) : new Point(clamped, -current.Y);
        }
        else if (_target is TextBoxBase && _vertical)
        {
            SendMessage(_target.Handle, EmLineScroll, IntPtr.Zero, (IntPtr)(clamped - ReadState().Position));
        }
        Sync();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || CurrentThumb() is not (int start, int length)) return;
        var along = (_vertical ? e.Y : e.X) - TrackPad;
        if (along >= start && along < start + length)
        {
            _dragOffset = along - start;
            Capture = true;
            Invalidate();
            return;
        }
        // A click on the track pages toward it, as the native bar does.
        ScrollTo(_state.Position + (along < start ? -_state.Page : _state.Page));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragOffset is not int offset || CurrentThumb() is not (_, int length)) return;
        var along = (_vertical ? e.Y : e.X) - TrackPad;
        ScrollTo(PositionAt(TrackLength, length, _state.Minimum, _state.Maximum, _state.Page, along - offset));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragOffset = null;
        Capture = false;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    // The wheel over the bar scrolls the target, as it would over the native bar.
    protected override void WndProc(ref Message m)
    {
        if ((m.Msg == WmMouseWheel || m.Msg == WmMouseHWheel) && _target.IsHandleCreated)
        {
            SendMessage(_target.Handle, m.Msg, m.WParam, m.LParam);
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Surface());
        if (CurrentThumb() is not (int start, int length)) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var active = _hover || _dragOffset != null;
        var thickness = active ? ThumbHoverThickness : ThumbThickness;
        var cross = _vertical ? Width : Height;
        var across = cross - EdgeInset - thickness; // grows away from the edge, as the web views' thumbs do
        var thumb = _vertical
            ? new RectangleF(across, TrackPad + start, thickness, length)
            : new RectangleF(TrackPad + start, across, length, thickness);
        var alpha = _dragOffset != null ? 204 : _hover ? 153 : 102;
        using var path = Pill(thumb);
        using var brush = new SolidBrush(Color.FromArgb(alpha, Theme.TextSecondary));
        g.FillPath(brush, path);
    }

    // What shows behind the native bar: the target's own background, else the nearest opaque one up the tree.
    private Color Surface()
    {
        for (Control? control = _target; control != null; control = control.Parent)
        {
            if (control.BackColor.A == 255) return control.BackColor;
        }
        return Theme.WindowBg;
    }

    private static GraphicsPath Pill(RectangleF r)
    {
        var d = Math.Min(r.Width, r.Height);
        var path = new GraphicsPath();
        if (r.Height >= r.Width)
        {
            path.AddArc(r.X, r.Y, d, d, 180, 180);
            path.AddArc(r.X, r.Bottom - d, d, d, 0, 180);
        }
        else
        {
            path.AddArc(r.X, r.Y, d, d, 90, 180);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        }
        path.CloseFigure();
        return path;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScrollInfo
    {
        public uint Size;
        public uint Mask;
        public int Minimum;
        public int Maximum;
        public uint Page;
        public int Position;
        public int TrackPosition;
    }

    [DllImport("user32.dll")]
    private static extern bool GetScrollInfo(IntPtr hwnd, int bar, ref ScrollInfo info);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}
