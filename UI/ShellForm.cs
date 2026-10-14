using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace NoisLogTray;

// The single app window: a sidebar of pages on the left, a header (title, notices, theme,
// language, window buttons) and the current page on the right. Borderless and rounded while
// Normal, edge-to-edge while maximized. Closing hides to the tray; TrayApp owns exit.
internal sealed class ShellForm : Form
{
    private static readonly int HeaderHeight = UiScale.S(48);
    private static readonly int BtnH = UiScale.S(30);
    private static readonly int BtnGap = UiScale.S(8);
    private static readonly int ToastWidth = UiScale.S(320);
    private static readonly int DefaultWidth = UiScale.S(1200);
    private static readonly int DefaultHeight = UiScale.S(800);
    private static readonly int WindowRadius = UiScale.S(12);

    // The form's own edge strip (Normal only) that Windows treats as the resize border.
    private static readonly int ResizeGrip = UiScale.S(6);
    private const int WmNcHitTest = 0x0084;
    private const int WmGetMinMaxInfo = 0x0024;

    private readonly SidebarNav _sidebar = new();
    private readonly Panel _rightPane = new();
    private readonly Panel _header = new();
    private readonly Panel _pageHost = new();
    private readonly Label _headerTitle = new();
    private readonly FlowLayoutPanel _headerButtons = new();
    private readonly WindowGlyphButton _menuBtn = new(WindowGlyphButton.GlyphKind.Menu);
    private readonly NotificationBell _bell = new();
    private readonly BusySpinner _spinner = new(BusyWork.Shared);
    private readonly WindowGlyphButton _minimizeBtn = new(WindowGlyphButton.GlyphKind.Minimize);
    private readonly WindowGlyphButton _maximizeBtn = new(WindowGlyphButton.GlyphKind.Maximize);
    private readonly CloseButton _closeBtn = new() { Surface = () => Theme.WindowBg };
    private readonly NoticeHistoryPopup _noticeHistory = new();
    private readonly NoticeToast _toast = new();
    private readonly ThemedToolTip _tips = new();

    // Recent notices for the bell's history, newest last; session-only, capped.
    private const int NoticeHistoryCap = 20;
    private readonly List<(DateTime Time, string Message, NoticeKind Kind, Action? Open)> _notices = new(); // Open: where a click goes

    private readonly Dictionary<ShellPage, Control> _pages = new();
    private readonly Dictionary<ShellPage, Control> _headerControls = new(); // shown beside the title on their page
    private ShellPage? _currentPage;

    // Header press while maximized; a drag past DragSize restores the window under the cursor.
    private Point? _dragStart;
    private FormWindowState _lastWindowState = FormWindowState.Normal;
    private bool? _savedMaximized;

    // The user may now be looking at another page: a page switch, the window shown or activated.
    internal event Action? PageViewed;

    // Static: the shell is rebuilt on every Settings save, and a Label does not own its Font.
    private static readonly Font TitleFont = UiScale.Font("Segoe UI Semibold", 13F, FontStyle.Bold);

    internal ShellForm()
    {
        Text = "NOIS Workday";
        Icon = AppIcon.Load(32);
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Theme.WindowBg;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
        Padding = new Padding(ResizeGrip);

        BuildLayout();

        var settings = AppSettings.Load();
        _savedMaximized = settings.WindowMaximized;
        _sidebar.Collapsed = settings.SidebarCollapsed;
        UpdateMinimumSize();
        RestoreWindowSize(settings);
        RestoreWindowPosition(settings);

        ApplyLanguage();
        Theme.Changed += ApplyTheme;
        Lang.Changed += ApplyLanguage;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= ApplyTheme;
            Lang.Changed -= ApplyLanguage;
            _noticeHistory.Dispose();
            _tips.Dispose();
        }
        base.Dispose(disposing);
    }

    internal ShellPage CurrentPage => _currentPage ?? ShellPage.Work;

    private void BuildLayout()
    {
        _sidebar.Dock = DockStyle.Left;
        _sidebar.PageSelected += ShowPage;

        _rightPane.Dock = DockStyle.Fill;
        _rightPane.BackColor = Theme.WindowBg;
        _pageHost.Dock = DockStyle.Fill;
        _pageHost.BackColor = Theme.WindowBg;

        BuildHeader();
        _rightPane.Controls.Add(_header);
        _rightPane.Controls.Add(_pageHost);
        _pageHost.BringToFront(); // Fill docks last, after the header takes the top strip

        _toast.Width = ToastWidth;
        _rightPane.Controls.Add(_toast);
        _toast.BringToFront(); // above the page
        _rightPane.Resize += (_, _) => PositionToast();

        Controls.Add(_rightPane);
        Controls.Add(_sidebar);
        _rightPane.BringToFront();
    }

    private void BuildHeader()
    {
        _header.Dock = DockStyle.Top;
        _header.Height = HeaderHeight;
        _header.BackColor = Theme.WindowBg;
        _header.Padding = new Padding(BtnGap, (HeaderHeight - BtnH) / 2, BtnGap, (HeaderHeight - BtnH) / 2);
        _header.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.CardBorder, 1f);
            e.Graphics.DrawLine(pen, 0, _header.Height - 1, _header.Width, _header.Height - 1);
        };

        _menuBtn.Dock = DockStyle.Left;
        _menuBtn.Width = BtnH;
        _menuBtn.Click += (_, _) => ToggleSidebar();

        // As wide as its text, so a page's header control sits right after it; the rest of the header drags.
        _headerTitle.Dock = DockStyle.Left;
        _headerTitle.AutoSize = true;
        _headerTitle.TextAlign = ContentAlignment.MiddleLeft;
        // Sized to its text and so drawn from the top of the header's row; the top padding lowers its letters (no
        // descenders, so they sit above the line's middle) onto the buttons' center.
        _headerTitle.Padding = new Padding(BtnGap, UiScale.S(1), BtnGap * 2, 0);
        _headerTitle.Font = TitleFont;
        _headerTitle.ForeColor = Theme.TextPrimary;
        _headerTitle.UseMnemonic = false;

        // Right to left: close, maximize/restore, minimize, a wider gap, bell, the busy spinner.
        _headerButtons.Dock = DockStyle.Right;
        _headerButtons.FlowDirection = FlowDirection.RightToLeft;
        _headerButtons.WrapContents = false;
        _headerButtons.AutoSize = true;
        _headerButtons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _headerButtons.Margin = Padding.Empty;
        _headerButtons.Padding = Padding.Empty;
        _headerButtons.BackColor = Theme.WindowBg;

        _closeBtn.Click += (_, _) => Close(); // OnFormClosing turns this into hide-to-tray
        _maximizeBtn.Click += (_, _) => ToggleMaximized();
        _minimizeBtn.Click += (_, _) => WindowState = FormWindowState.Minimized;
        _bell.Click += (_, _) => OpenNoticeHistory();

        var buttons = new Control[] { _closeBtn, _maximizeBtn, _minimizeBtn, _bell, _spinner };
        foreach (var button in buttons)
        {
            button.Size = new Size(BtnH, BtnH);
            button.Margin = new Padding(BtnGap / 2, 0, BtnGap / 2, 0);
        }
        _bell.Margin = new Padding(BtnGap / 2, 0, BtnGap * 2, 0); // separates the window buttons
        _headerButtons.Controls.AddRange(buttons);

        // There is no title bar, so the header (and its title) drags the window.
        foreach (var dragSurface in new Control[] { _header, _headerTitle })
        {
            dragSurface.MouseDown += OnHeaderMouseDown;
            dragSurface.MouseMove += OnHeaderMouseMove;
            dragSurface.MouseUp += (_, _) => _dragStart = null;
        }

        _header.Controls.Add(_headerButtons);
        _header.Controls.Add(_menuBtn);
        _header.Controls.Add(_headerTitle);
        _headerTitle.BringToFront(); // docks after the menu button, from the left
    }

    private void ToggleSidebar()
    {
        _sidebar.Collapsed = !_sidebar.Collapsed;
        var settings = AppSettings.Load(); // read-modify-write to keep other keys
        settings.SidebarCollapsed = _sidebar.Collapsed;
        AppSettings.Save(settings);
    }

    private void ToggleMaximized() =>
        WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;

    // ---- Pages

    // headerControl sits in the header right after the title, while the page is current: docked Left unless it chose a
    // Dock itself (a HeaderToolbar fills the room up to the header's buttons).
    internal void AddPage(ShellPage page, Control content, Control? headerControl = null)
    {
        content.Dock = DockStyle.Fill;
        content.Visible = false;
        _pageHost.Controls.Add(content);
        _pages[page] = content;
        if (headerControl != null)
        {
            if (headerControl.Dock == DockStyle.None) headerControl.Dock = DockStyle.Left;
            headerControl.Visible = false;
            _header.Controls.Add(headerControl);
            headerControl.BringToFront(); // docks after the title
            _headerControls[page] = headerControl;
        }
        UpdateMinimumSize();
    }

    internal void ShowPage(ShellPage page)
    {
        if (!_pages.TryGetValue(page, out var content)) return;
        if (_currentPage == page && Visible) return;

        // Redraw off while the pages swap and refresh, then one repaint: no flicker, without the
        // whole-window cost of WS_EX_COMPOSITED (which made every repaint slow).
        var redrawOff = _pageHost.IsHandleCreated;
        if (redrawOff) SendMessage(_pageHost.Handle, 0x000B /* WM_SETREDRAW */, IntPtr.Zero, IntPtr.Zero);
        _pageHost.SuspendLayout();
        content.Visible = true;
        if (_currentPage is ShellPage previous && previous != page) _pages[previous].Visible = false;
        _currentPage = page;
        _headerTitle.Text = SidebarNav.PageLabel(page);
        foreach (var (headerPage, headerControl) in _headerControls) headerControl.Visible = headerPage == page;
        _sidebar.Selected = page;
        if (IsHandleCreated && Visible) (content as IShellPage)?.OnPageShown();
        _pageHost.ResumeLayout();
        PageViewed?.Invoke();
        if (redrawOff)
        {
            SendMessage(_pageHost.Handle, 0x000B /* WM_SETREDRAW */, (IntPtr)1, IntPtr.Zero);
            _pageHost.Invalidate(true);
        }
    }

    internal void SetBadge(ShellPage page, int count) => _sidebar.SetBadge(page, count);

    private IShellPage? CurrentShellPage =>
        _currentPage is ShellPage page ? _pages[page] as IShellPage : null;

    // Not OnShown: it is posted later and would fire OnPageShown a second time.
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) CurrentShellPage?.OnPageShown();
        if (Visible) PageViewed?.Invoke();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        CurrentShellPage?.OnActivatedRefresh();
        PageViewed?.Invoke();
    }

    // Wide enough for the open sidebar and the widest page, whatever the sidebar state.
    private void UpdateMinimumSize()
    {
        var contentWidth = _pages.Values.Select(c => c.MinimumSize.Width).DefaultIfEmpty(0).Max();
        var contentHeight = _pages.Values.Select(c => c.MinimumSize.Height).DefaultIfEmpty(0).Max();
        MinimumSize = new Size(
            UiScale.S(SidebarNav.ExpandedWidth) + contentWidth + 2 * ResizeGrip,
            HeaderHeight + contentHeight + 2 * ResizeGrip);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        UpdateMinimumSize();
    }

    // ---- Window bounds

    // Saved size clamped to the screen, or 1200x800 logical the first time, so the first restore
    // after opening maximized does not fall back to a tiny window.
    private void RestoreWindowSize(AppSettings settings)
    {
        var workingArea = SavedLocationScreen(settings)?.WorkingArea ?? Screen.PrimaryScreen!.WorkingArea;
        var width = settings.WindowWidth ?? DefaultWidth;
        var height = settings.WindowHeight ?? DefaultHeight;
        ClientSize = new Size(
            Math.Clamp(width, MinimumSize.Width, Math.Max(MinimumSize.Width, workingArea.Width)),
            Math.Clamp(height, MinimumSize.Height, Math.Max(MinimumSize.Height, workingArea.Height)));
    }

    // The last position if it still lands on a connected monitor; otherwise centered on the primary.
    private void RestoreWindowPosition(AppSettings settings)
    {
        StartPosition = FormStartPosition.Manual;
        if (SavedLocationScreen(settings) != null)
        {
            Location = new Point(settings.WindowX!.Value, settings.WindowY!.Value);
            return;
        }
        var workingArea = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(
            workingArea.X + (workingArea.Width - Width) / 2,
            workingArea.Y + (workingArea.Height - Height) / 2);
    }

    private static Screen? SavedLocationScreen(AppSettings settings)
    {
        if (settings.WindowX is not int x || settings.WindowY is not int y) return null;
        return Screen.AllScreens.FirstOrDefault(s => s.WorkingArea.Contains(new Point(x, y)));
    }

    // Normal bounds (RestoreBounds while maximized) plus the maximized flag, read-modify-write.
    // The flag is written only once the window exists, so constructor sizing never overwrites it.
    private void SaveWindowBounds()
    {
        if (WindowState == FormWindowState.Minimized) return;
        var maximized = WindowState == FormWindowState.Maximized;
        var bounds = maximized ? RestoreBounds : new Rectangle(Location, ClientSize);
        var settings = AppSettings.Load();
        settings.WindowX = bounds.X;
        settings.WindowY = bounds.Y;
        settings.WindowWidth = bounds.Width;
        settings.WindowHeight = bounds.Height;
        if (IsHandleCreated && maximized != _savedMaximized)
        {
            settings.WindowMaximized = maximized;
            _savedMaximized = maximized;
        }
        AppSettings.Save(settings);
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        SaveWindowBounds();
    }

    // ---- Borderless chrome: shadow, taskbar min/max, rounded corners and border while Normal.

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            cp.Style |= 0x00020000;      // WS_MINIMIZEBOX: the taskbar button still minimizes/restores
            cp.Style |= 0x00010000;      // WS_MAXIMIZEBOX: Win+Up and Aero Snap can maximize
            return cp;
        }
    }

    // Minimized, the client area is 0x0: a layout would shrink every page to nothing and re-render it,
    // then again on restore. Skipping it keeps the pages at their size, so restoring changes nothing.
    protected override void OnLayout(LayoutEventArgs levent)
    {
        if (WindowState == FormWindowState.Minimized || ClientSize.Width == 0 || ClientSize.Height == 0) return;
        base.OnLayout(levent);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (WindowState != _lastWindowState)
        {
            _lastWindowState = WindowState;
            _maximizeBtn.Glyph = WindowState == FormWindowState.Maximized
                ? WindowGlyphButton.GlyphKind.Restore
                : WindowGlyphButton.GlyphKind.Maximize;
            ApplyLanguage(); // the maximize button's tooltip follows the state
            if (IsHandleCreated) SaveWindowBounds();
        }

        if (WindowState == FormWindowState.Minimized || ClientSize.Width == 0 || ClientSize.Height == 0) return;
        var oldRegion = Region;
        if (WindowState == FormWindowState.Maximized)
        {
            Region = null;
            Padding = Padding.Empty;
        }
        else
        {
            using var path = RoundedPath(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), WindowRadius);
            Region = new Region(path);
            Padding = new Padding(ResizeGrip);
        }
        oldRegion?.Dispose();
        Invalidate();
    }

    // Drawn on the form, not a child: the children are inset by the resize grip.
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (WindowState != FormWindowState.Normal) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedPath(new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1), WindowRadius);
        using var pen = new Pen(Theme.CardBorder, 1f);
        e.Graphics.DrawPath(pen, path);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaxSize;
        public Point MaxPosition;
        public Point MinTrackSize;
        public Point MaxTrackSize;
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WmGetMinMaxInfo)
        {
            // A borderless window maximizes over the taskbar, and Windows scales ptMaxSize
            // when a secondary monitor is larger than the primary; pin both to the work area.
            var screen = Screen.FromHandle(m.HWnd);
            var info = Marshal.PtrToStructure<MinMaxInfo>(m.LParam);
            var maximized = ShellLayout.MaximizedRect(screen.Bounds, screen.WorkingArea);
            info.MaxPosition = maximized.Location;
            info.MaxSize = new Point(maximized.Width, maximized.Height);
            info.MaxTrackSize = new Point(screen.WorkingArea.Width, screen.WorkingArea.Height);
            Marshal.StructureToPtr(info, m.LParam, false);
            return;
        }

        if (m.Msg != WmNcHitTest || WindowState != FormWindowState.Normal) return;
        var lParam = m.LParam.ToInt64();
        var cursor = PointToClient(new Point((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF)));
        var hit = ResizeHitTest.At(cursor, ClientSize, ResizeGrip, WindowRadius);
        if (hit != ResizeHitTest.None) m.Result = hit;
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attachThreadId, uint attachToThreadId, bool attach);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    // Windows refuses the foreground to a background process (a balloon click is one) and only flashes the taskbar
    // button; sharing the foreground thread's input state for the call lets it through.
    internal void ForceForeground()
    {
        var foregroundThreadId = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
        var ownThreadId = GetCurrentThreadId();
        var attached = foregroundThreadId != 0 && foregroundThreadId != ownThreadId && AttachThreadInput(ownThreadId, foregroundThreadId, true);
        try
        {
            SetForegroundWindow(Handle);
            Activate();
        }
        finally
        {
            if (attached) AttachThreadInput(ownThreadId, foregroundThreadId, false);
        }
    }

    // Hand the drag to Windows as a title-bar drag; ResizeEnd then saves the new position.
    private void DragWindow()
    {
        ReleaseCapture();
        SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
    }

    private void OnHeaderMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (e.Clicks == 2)
        {
            ToggleMaximized();
            return;
        }
        if (WindowState == FormWindowState.Normal) DragWindow();
        else if (WindowState == FormWindowState.Maximized) _dragStart = e.Location;
    }

    // Dragging a maximized header restores the window, keeping the cursor at the same
    // relative x and the same y, so it stays on the header even with the sidebar to its left.
    private void OnHeaderMouseMove(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _dragStart is not Point start || WindowState != FormWindowState.Maximized) return;
        var dragSize = SystemInformation.DragSize;
        if (Math.Abs(e.X - start.X) <= dragSize.Width && Math.Abs(e.Y - start.Y) <= dragSize.Height) return;

        _dragStart = null;
        var cursor = Cursor.Position;
        var p = PointToClient(cursor);
        var ratio = p.X / (double)ClientSize.Width;
        WindowState = FormWindowState.Normal;
        Left = cursor.X - (int)(ratio * Width);
        Top = cursor.Y - p.Y;
        DragWindow();
    }

    private static GraphicsPath RoundedPath(Rectangle r, int radius)
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

    // ---- Keyboard, close

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.D1:
            case Keys.Control | Keys.NumPad1:
                ShowPage(ShellPage.Dashboard);
                return true;
            case Keys.Control | Keys.D2:
            case Keys.Control | Keys.NumPad2:
                ShowPage(ShellPage.Work);
                return true;
            case Keys.Control | Keys.D3:
            case Keys.Control | Keys.NumPad3:
                ShowPage(ShellPage.Claude);
                return true;
            case Keys.Control | Keys.D4:
            case Keys.Control | Keys.NumPad4:
                ShowPage(ShellPage.Ticket);
                return true;
        }

        // In the terminal pages Esc belongs to the page (it interrupts a running Claude turn), while a tab is open there.
        if (keyData != Keys.Escape
            || (CurrentPage == ShellPage.Claude && _pages[ShellPage.Claude] is ClaudePage claudePage && claudePage.HasOpenTabs)
            || (CurrentPage == ShellPage.Ticket && _pages[ShellPage.Ticket] is TicketWorkspacePage ticketPage && ticketPage.HasOpenTabs))
            return base.ProcessCmdKey(ref msg, keyData);

        // Esc clears a non-empty input box first (placeholder marks the inputs, which skips
        // the inline hours field); otherwise it hides the window to the tray.
        var focused = ActiveControl;
        while (focused is ContainerControl container && container.ActiveControl != null) focused = container.ActiveControl;
        if (focused is TextBox textBox && !string.IsNullOrEmpty(textBox.PlaceholderText) && textBox.TextLength != 0)
        {
            textBox.Clear();
            return true;
        }
        Close(); // OnFormClosing turns a user close into hide-to-tray
        return true;
    }

    // Hide to tray on a user close instead of exiting the process.
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            SaveWindowBounds();
            foreach (var page in _pages.Values) (page as IShellPage)?.OnHidden();
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    // ---- Theme, language

    private void ApplyTheme()
    {
        BackColor = Theme.WindowBg;
        _rightPane.BackColor = Theme.WindowBg;
        _header.BackColor = Theme.WindowBg;
        _headerButtons.BackColor = Theme.WindowBg;
        _pageHost.BackColor = Theme.WindowBg;
        _headerTitle.ForeColor = Theme.TextPrimary;
        Invalidate(true);
    }

    private void ApplyLanguage()
    {
        if (_currentPage is ShellPage page) _headerTitle.Text = SidebarNav.PageLabel(page);

        var collapseText = Lang.T("Collapse or expand the menu", "Thu gọn / mở rộng menu");
        var minimizeText = Lang.T("Minimize", "Thu nhỏ");
        var maximizeText = WindowState == FormWindowState.Maximized
            ? Lang.T("Restore down", "Phục hồi kích thước")
            : Lang.T("Maximize", "Phóng to");
        var hideText = Lang.T("Hide to tray", "Ẩn xuống khay");

        _tips.SetToolTip(_menuBtn, collapseText);
        _menuBtn.AccessibleName = collapseText;
        _tips.SetToolTip(_bell, Lang.T("Notifications", "Thông báo"));
        _tips.SetToolTip(_minimizeBtn, minimizeText);
        _minimizeBtn.AccessibleName = minimizeText;
        _tips.SetToolTip(_maximizeBtn, maximizeText);
        _maximizeBtn.AccessibleName = maximizeText;
        _tips.SetToolTip(_closeBtn, hideText);
        _closeBtn.AccessibleName = hideText;

        if (_noticeHistory.Visible) RenderNoticeHistory();
    }

    // ---- Notices

    // Record a notice in the bell history and, when the window is on screen, show the toast.
    // With the history open the history itself is refreshed instead. Safe from any thread.
    internal void PostNotice(string message, NoticeKind kind, Action? open = null)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string, NoticeKind, Action?>(PostNotice), message, kind, open);
            return;
        }

        _notices.Add((DateTime.Now, message, kind, open));
        if (_notices.Count > NoticeHistoryCap) _notices.RemoveAt(0);

        if (_noticeHistory.Visible)
        {
            RenderNoticeHistory();
            return;
        }
        _bell.HasUnread = true;
        if (IsOnScreen)
        {
            PositionToast();
            _toast.ShowNotice(message, kind, open: open);
        }
        else
        {
            _toast.HideNotice(); // drop a stale "Logging..." so it is not there on reopen
        }
    }

    // A sticky toast naming the running action; null hides it unless a result already replaced it.
    internal void ShowBusy(string? text)
    {
        if (text != null)
        {
            PositionToast();
            _toast.ShowNotice(text, NoticeKind.Info, sticky: true);
        }
        else if (_toast.IsSticky)
        {
            _toast.HideNotice();
        }
    }

    internal bool IsOnScreen => Visible && WindowState != FormWindowState.Minimized;

    // The window the user is looking at right now; a window covered by another app is not.
    internal bool IsForeground => IsOnScreen && ActiveForm == this;

    // Right-aligned under the bell, read from the bell's real position (it moves with the header).
    private void PositionToast()
    {
        if (!_bell.IsHandleCreated || !_rightPane.IsHandleCreated) return;
        var bellCorner = _rightPane.PointToClient(_bell.PointToScreen(new Point(_bell.Width, _bell.Height)));
        _toast.Location = new Point(Math.Max(0, bellCorner.X - _toast.Width), bellCorner.Y + UiScale.S(6));
    }

    private void OpenNoticeHistory()
    {
        _toast.HideNotice();
        _bell.HasUnread = false;
        RenderNoticeHistory();
        _noticeHistory.Show(_bell, new Point(_bell.Width, _bell.Height + UiScale.S(6)), ToolStripDropDownDirection.BelowLeft);
    }

    // Right-aligned under the bell, so it may span back to the window's left margin.
    private void RenderNoticeHistory()
    {
        var bellRight = PointToClient(_bell.PointToScreen(new Point(_bell.Width, 0))).X;
        _noticeHistory.SetNotices(_notices, Math.Min(UiScale.S(400), bellRight - UiScale.S(20)));
    }
}
