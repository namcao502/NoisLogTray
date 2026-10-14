using System.Drawing;

namespace NoisLogTray;

// One drawn cell of a ticket table row; ToolTip, when given, is what the cell's tooltip says.
internal sealed record TableCell(TaskTableColumn Column, string Text, Color Color, string? ToolTip = null);
