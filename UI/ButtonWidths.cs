using System.Drawing;

namespace NoisLogTray;

// Fixed button widths, so a label change (Queue -> Queued, EN -> VI) never resizes or shifts a button.
// Each width is measured once from every label that kind of button can show, in both languages.
internal static class ButtonWidths
{
    internal const string ReadyToTestText = "Ready to Test";

    private static readonly string[] Labels =
    {
        "Queue", "Queued", "Đã queue", "Open", "Mở", "Start", "Bắt đầu", "Done", "Xong", "→ Done", "→ Xong",
        "Dismiss", "Ẩn", "Restore", "Khôi phục", "Seen", "Đã xem",
    };

    private static readonly string[] ToolbarLabels =
    {
        "Log queue now", "Log queue ngay", "Log cả hàng đợi", "Clear queue", "Xóa hàng đợi", "Edit JQL", "Sửa JQL",
        "Refresh", "Làm mới", "Check", "Check now", "Kiểm tra", "Add", "Thêm",
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

    // Card-header toolbar buttons (Log queue now, Refresh, Check, Add, ...).
    internal static int ToolbarWidth(Font font)
    {
        if (_toolbarWidth == 0) _toolbarWidth = ToolbarLabels.Max(label => TextRenderer.MeasureText(label, font).Width) + UiScale.S(24);
        return _toolbarWidth;
    }
}
