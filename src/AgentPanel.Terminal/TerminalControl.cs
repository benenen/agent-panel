using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;

namespace AgentPanel.Terminal;

public sealed class TerminalControl : Control
{
    private const double CellWidth = 9, CellHeight = 19, Padding = 12;
    private GhosttySession? _session;
    public GhosttySession? Session
    {
        get => _session;
        set { _session = value; ResizeSession(); InvalidateVisual(); }
    }

    public TerminalControl() { Focusable = true; ClipToBounds = true; }

    protected override void OnSizeChanged(SizeChangedEventArgs e) { base.OnSizeChanged(e); ResizeSession(); }
    private void ResizeSession()
    {
        if (Bounds.Width > 30 && Bounds.Height > 30)
            Session?.Resize((int)((Bounds.Width - 2 * Padding) / CellWidth), (int)((Bounds.Height - 2 * Padding) / CellHeight));
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Brush.Parse("#18181B"), new Rect(Bounds.Size));
        if (Session is null)
        {
            context.DrawText(Text("选择一个会话，点击「启动 / 重启」进入终端。", 0xff7f8ca5), new Point(24, 30));
            return;
        }
        var snapshot = Session.Snapshot();
        for (var i = 0; i < snapshot.Count; i++)
        {
            var cell = snapshot.Cells[i];
            var x = Padding + i % Session.Columns * CellWidth;
            var y = Padding + i / Session.Columns * CellHeight;
            if (cell.Background != 0xff18181b)
                context.FillRectangle(new SolidColorBrush(Color.FromUInt32(cell.Background)), new Rect(x, y, CellWidth, CellHeight));
            var text = cell.GetText();
            if (text.Length > 0 && (cell.Flags & 8) == 0)
                context.DrawText(Text(text, cell.Foreground, cell.Flags), new Point(x, y));
            if ((cell.Flags & 4) != 0)
                context.DrawLine(new Pen(new SolidColorBrush(Color.FromUInt32(cell.Foreground))), new Point(x, y + 17), new Point(x + CellWidth, y + 17));
        }
        if (snapshot.CursorX >= 0 && !Session.HasExited)
            context.DrawRectangle(new Pen(Brush.Parse(IsFocused ? "#A1A1AA" : "#696970")),
                new Rect(Padding + snapshot.CursorX * CellWidth, Padding + snapshot.CursorY * CellHeight, CellWidth, CellHeight));
    }

    private static FormattedText Text(string text, uint color, uint flags = 0) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("DejaVu Sans Mono", (flags & 2) != 0 ? FontStyle.Italic : FontStyle.Normal,
                (flags & 1) != 0 ? FontWeight.Bold : FontWeight.Normal), 14, new SolidColorBrush(Color.FromUInt32(color)));

    protected override void OnPointerPressed(PointerPressedEventArgs e) { base.OnPointerPressed(e); Focus(); e.Handled = true; }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        Session?.Scroll(-(int)(e.Delta.Y * 3)); InvalidateVisual(); e.Handled = true;
    }
    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (e.Text is not null) Session?.Write(e.Text);
        e.Handled = true;
    }
    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Session is null) return;
        if (e.Key == Key.V && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            e.Handled = true;
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            var target = Session;
            if (clipboard is not null)
            {
                var text = await clipboard.TryGetTextAsync();
                if (text is not null && ReferenceEquals(Session, target)) target.Paste(text);
            }
            return;
        }
        var key = e.Key switch
        {
            Key.Enter => 1,
            Key.Back => 2,
            Key.Tab => 3,
            Key.Escape => 4,
            Key.Up => 5,
            Key.Down => 6,
            Key.Right => 7,
            Key.Left => 8,
            Key.Home => 9,
            Key.End => 10,
            Key.Delete => 11,
            Key.Insert => 12,
            Key.PageUp => 13,
            Key.PageDown => 14,
            _ => 0
        };
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt)) != 0 && e.Key >= Key.A && e.Key <= Key.Z)
            key = 100 + e.Key - Key.A;
        if (e.Key >= Key.F1 && e.Key <= Key.F12) key = 200 + e.Key - Key.F1;
        if (key == 0) return;
        var mods = (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 1 : 0) |
            (e.KeyModifiers.HasFlag(KeyModifiers.Control) ? 2 : 0) | (e.KeyModifiers.HasFlag(KeyModifiers.Alt) ? 4 : 0);
        Session.Key(key, mods); e.Handled = true;
    }
}
