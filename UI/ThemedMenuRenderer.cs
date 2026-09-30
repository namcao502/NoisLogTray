using System.Drawing;

namespace NoisLogTray;

// Renders a ContextMenuStrip in the current Theme palette. Colors are read at paint
// time, so a menu opened after a theme switch picks up the new palette.
internal sealed class ThemedMenuRenderer : ToolStripProfessionalRenderer
{
    internal ThemedMenuRenderer() : base(new ThemedColors()) => RoundedEdges = false;

    // Build a themed dropdown for an action button.
    internal static ContextMenuStrip CreateMenu()
    {
        var menu = new ContextMenuStrip { Renderer = new ThemedMenuRenderer(), ShowImageMargin = false };
        menu.Font = new Font("Segoe UI", 9.5F);
        return menu;
    }

    // Open the menu right under its anchor button, left edges aligned.
    internal static void ShowBelow(ContextMenuStrip menu, Control anchor) =>
        menu.Show(anchor, new Point(0, anchor.Height + 2));

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.TextPrimary : Theme.TextSecondary;
        base.OnRenderItemText(e);
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
