using System.Runtime.InteropServices;
using System.Text;

namespace AgentPanel.Terminal;

public sealed unsafe class GhosttySession : IDisposable
{
    private nint _handle;
    private Cell[] _cells = [];
    public int Columns { get; private set; } = 80;
    public int Rows { get; private set; } = 24;
    public bool HasExited { get; private set; }
    public string? Error { get; private set; }

    public GhosttySession(string directory, string command)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("初始化版本的 PTY 桥接仅支持 Linux。");
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        _handle = Native.Create(directory, command, Columns, Rows);
        if (_handle == 0) throw new IOException("无法创建 Ghostty 终端或 PTY。");
    }

    public bool Pump()
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        if (HasExited) return false;
        var result = Native.Pump(_handle);
        if (result < 0) HasExited = true;
        return result != 0;
    }

    public void Write(string text)
    {
        if (_handle == 0 || HasExited) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        if (Native.Write(_handle, bytes, (nuint)bytes.Length) != 0) Error = "终端输入失败，进程可能已退出。";
        Native.Scroll(_handle, int.MaxValue);
    }

    public void Resize(int columns, int rows)
    {
        if (_handle == 0 || (columns == Columns && rows == Rows)) return;
        columns = Math.Clamp(columns, 2, 500); rows = Math.Clamp(rows, 2, 200);
        if (Native.Resize(_handle, columns, rows) != 0) throw new IOException("调整终端大小失败。");
        Columns = columns; Rows = rows;
    }

    public void Key(int key, int modifiers)
    {
        if (_handle != 0 && !HasExited && Native.Key(_handle, key, modifiers) != 0)
            Error = "终端按键编码失败。";
    }

    public void Paste(string text)
    {
        if (_handle == 0 || HasExited) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        if (Native.Paste(_handle, bytes, (nuint)bytes.Length) != 0) Error = "终端粘贴失败。";
    }

    public (Cell[] Cells, int Count, int CursorX, int CursorY) Snapshot()
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        if (_cells.Length != Columns * Rows) _cells = new Cell[Columns * Rows];
        fixed (Cell* cells = _cells)
        {
            var count = Native.Snapshot(_handle, cells, _cells.Length, out var x, out var y);
            if (count < 0) throw new IOException("读取终端画面失败。");
            return (_cells, count, x, y);
        }
    }

    public void Scroll(int lines) { if (_handle != 0) Native.Scroll(_handle, lines); }
    public void Dispose()
    {
        if (_handle == 0) return;
        Native.Destroy(_handle); _handle = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Cell
    {
        public uint Foreground, Background, Flags;
        public fixed byte Text[64];
        public string GetText()
        {
            fixed (byte* text = Text)
            {
                var length = 0;
                while (length < 64 && text[length] != 0) length++;
                return Encoding.UTF8.GetString(text, length);
            }
        }
    }

    private static class Native
    {
        private const string Library = "agent_panel_ghostty";
        [DllImport(Library, EntryPoint = "ap_create", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint Create([MarshalAs(UnmanagedType.LPUTF8Str)] string directory,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string command, int columns, int rows);
        [DllImport(Library, EntryPoint = "ap_pump", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Pump(nint terminal);
        [DllImport(Library, EntryPoint = "ap_write", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Write(nint terminal, byte[] bytes, nuint length);
        [DllImport(Library, EntryPoint = "ap_key", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Key(nint terminal, int key, int modifiers);
        [DllImport(Library, EntryPoint = "ap_paste", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Paste(nint terminal, byte[] bytes, nuint length);
        [DllImport(Library, EntryPoint = "ap_resize", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Resize(nint terminal, int columns, int rows);
        [DllImport(Library, EntryPoint = "ap_snapshot", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Snapshot(nint terminal, Cell* cells, int capacity, out int cursorX, out int cursorY);
        [DllImport(Library, EntryPoint = "ap_scroll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Scroll(nint terminal, int lines);
        [DllImport(Library, EntryPoint = "ap_destroy", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Destroy(nint terminal);
    }
}
