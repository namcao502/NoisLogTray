using System.Drawing;
using System.Drawing.Drawing2D;

namespace NoisLogTray;

// Renders a ContextMenuStrip in the current Theme palette, as a card like the bell's history: rounded corners and
// border, roomy rows, a rounded hover, the Fluent checkmark in the accent color and inset separators. Colors are
// read at paint time, so a menu opened after a theme switch picks up the new palette.
internal sealed class ThemedMenuRenderer : ToolStripProfessionalRenderer
{
    private static readonly int Radius = UiScale.S(8);
    private static readonly int HoverRadius = UiScale.S(5);
    private static readonly int HoverInset = UiScale.S(4);
    private static readonly int SeparatorInset = UiScale.S(10);

    internal ThemedMenuRenderer() : base(new ThemedColors()) => RoundedEdges = false;

    // Build a themed dropdown for an action button.
    internal static ContextMenuStrip CreateMenu()
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new ThemedMenuRenderer(),
            ShowImageMargin = false,
            Padding = UiScale.S(new Padding(2, 6, 2, 6)),
        };
        menu.Font = UiScale.Font("Segoe UI", 9.5F);
        // Items come from the callers (and the Folder menu rebuilds its own on every open), so each gets its row
        // height here.
        menu.ItemAdded += (_, e) =>
        {
            if (e.Item is ToolStripMenuItem) e.Item.Padding = UiScale.S(new Padding(6, 5, 10, 5));
        };
        menu.SizeChanged += (_, _) => ClipToRoundedCard(menu);
        return menu;
    }

    // Open the menu right under its anchor button, right edges aligned, so a menu wider than
    // the button grows into the window instead of past its right edge.
    internal static void ShowBelow(ContextMenuStrip menu, Control anchor) =>
        menu.Show(anchor, new Point(anchor.Width, anchor.Height + UiScale.S(2)), ToolStripDropDownDirection.BelowLeft);

    private static void ClipToRoundedCard(ContextMenuStrip menu)
    {
        var oldRegion = menu.Region;
        using (var path = RoundedPath(new Rectangle(0, 0, menu.Width, menu.Height), Radius))
            menu.Region = new Region(path);
        oldRegion?.Dispose();
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedPath(new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1), Radius);
        using var pen = new Pen(Theme.CardBorder, 1f);
        e.Graphics.DrawPath(pen, path);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(HoverInset, 0, e.Item.Width - 2 * HoverInset - 1, e.Item.Height - 1);
        using var path = RoundedPath(bounds, HoverRadius);
        using var brush = new SolidBrush(Theme.Hover);
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        // The check column is a little smaller than the glyph's em box; draw in a padded box so it is never clipped.
        var box = Rectangle.Inflate(CenteredInItem(e.ImageRectangle, e.Item), UiScale.S(4), UiScale.S(4));
        Icons.Draw(e.Graphics, Icons.Checkmark, Icons.Small, box, Theme.Accent);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.Height / 2;
        using var pen = new Pen(Theme.Divider);
        e.Graphics.DrawLine(pen, SeparatorInset, y, e.Item.Width - SeparatorInset, y);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.TextPrimary : Theme.TextSecondary;
        e.TextRectangle = CenteredInItem(e.TextRectangle, e.Item);
        base.OnRenderItemText(e);
    }

    // A dropdown lays out text and check once for the whole menu, centered in a row height that leaves out the
    // item's Padding, so with the padding CreateMenu adds they would sit at the top of the row.
    private static Rectangle CenteredInItem(Rectangle r, ToolStripItem item) =>
        new(r.X, (item.Height - r.Height) / 2, r.Width, r.Height);

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

    private sealed class ThemedColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.CardSurface;
        public override Color MenuBorder => Theme.CardBorder;
        public override Color MenuItemBorder => Theme.Hover;
        public override Color MenuItemSelected => Theme.Hover;
        public override Color SeparatorDark => Theme.Divider;
        public override Color SeparatorLight => Theme.CardSurface;
        public override Color ImageMarginGradientBegin => Theme.CardSurface;
        public override Color ImageMarginGradientMiddle => Theme.CardSurface;
        public override Color ImageMarginGradientEnd => Theme.CardSurface;
    }
}
