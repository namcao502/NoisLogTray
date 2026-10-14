using System.Drawing;

namespace NoisLogTray;

// Fixed button widths, so a label change (Queue -> Queued, EN -> VI) never resizes or shifts a button.
// Each width is measured once from every label that kind of button can show, in both languages.
internal static class ButtonWidths
{
    internal const string ReadyToTestText = "Ready to Test";

    private static readonly string[] Labels =
    {
        // "→ Done" stands for Done with its arrow glyph (TaskRow, DashboardRow), about as wide; so do "→ Log" and
        // "→ In entry" for the "+ Log" button and its checkmark.
        "Queue", "Queued", "Đã queue", "Open", "Mở", "Start", "Bắt đầu", "Done", "Xong", "→ Done", "→ Xong",
        "Restore", "Khôi phục", "Seen", "Đã xem", "→ Log", "→ In entry", "→ Đã thêm",
        "View PR", "Xem PR", "Clean up", "Dọn dẹp", "Log",
    };

    private static readonly string[] ToolbarLabels =
    {
        "Log queue now", "Log queue ngay", "Log cả hàng đợi", "Clear queue", "Xóa hàng đợi",
        "Refresh", "Làm mới", "Check", "Check now", "Kiểm tra", "Add", "Thêm",
        "Re-check", "Kiểm tra lại", "All tickets", "Tất cả ticket",
    };

    private static int _actionWidth;
    private static int _toolbarWidth;
    private static int _readyToTestWidth;

    internal static int ActionWidth(Font font)
    {
        if (_actionWidth == 0) _actionWidth = Labels.Max(label => TextRenderer.MeasureText(label, font).Width) + UiScale.S(24);
        return _actionWidth;
    }

    internal static int ReadyToTestWidth(Font font)
    {
        if (_readyToTestWidth == 0) _readyToTestWidth = TextRenderer.MeasureText(ReadyToTestText, font).Width + UiScale.S(24);
        return _readyToTestWidth;
    }

    // Card-header toolbar buttons (Log queue now, Refresh, Check, Add, All tickets, the Dashboard's Re-check, ...).
    internal static int ToolbarWidth(Font font)
    {
        if (_toolbarWidth == 0) _toolbarWidth = ToolbarLabels.Max(label => TextRenderer.MeasureText(label, font).Width) + UiScale.S(24);
        return _toolbarWidth;
    }
}
