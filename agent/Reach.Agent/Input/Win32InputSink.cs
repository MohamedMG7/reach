using System.Drawing;
using System.Runtime.InteropServices;

namespace Reach.Agent.Input;

/// <summary>Maps a pixel on the virtual desktop to SendInput's 0..65535 absolute coordinates.</summary>
public static class AbsoluteMouse
{
    public static Point Clamp(Point p, Rectangle desktop) =>
        new(Math.Clamp(p.X, desktop.Left, desktop.Right - 1), Math.Clamp(p.Y, desktop.Top, desktop.Bottom - 1));

    public static (int X, int Y) Normalize(Point p, Rectangle desktop) =>
        (Scale(p.X - desktop.Left, desktop.Width), Scale(p.Y - desktop.Top, desktop.Height));

    private static int Scale(int offset, int size) => size <= 1 ? 0 : (int)Math.Round(offset * 65535.0 / (size - 1));
}

/// <summary>Real input via Win32 SendInput. Requires a per-monitor DPI aware process (see the csproj).</summary>
public sealed class Win32InputSink : IInputSink
{
    private const uint InputMouse = 0, InputKeyboard = 1;
    private const uint MouseMove = 0x0001, MouseAbsolute = 0x8000, MouseVirtualDesk = 0x4000;
    private const uint MouseWheel = 0x0800, MouseHWheel = 0x1000;
    private const uint KeyExtended = 0x0001, KeyUp = 0x0002, KeyUnicode = 0x0004;
    private const int SmXVirtualScreen = 76, SmYVirtualScreen = 77, SmCxVirtualScreen = 78, SmCyVirtualScreen = 79;

    public void MoveBy(int dx, int dy)
    {
        if (!GetCursorPos(out var pos)) return;
        var desktop = new Rectangle(
            GetSystemMetrics(SmXVirtualScreen), GetSystemMetrics(SmYVirtualScreen),
            GetSystemMetrics(SmCxVirtualScreen), GetSystemMetrics(SmCyVirtualScreen));
        var target = AbsoluteMouse.Clamp(new Point(pos.X + dx, pos.Y + dy), desktop);
        var (x, y) = AbsoluteMouse.Normalize(target, desktop);
        Send(Mouse(x, y, 0, MouseMove | MouseAbsolute | MouseVirtualDesk));
    }

    public void Button(MouseButtonKind button, bool down)
    {
        uint flag = button switch
        {
            MouseButtonKind.Left => down ? 0x0002u : 0x0004u,
            MouseButtonKind.Right => down ? 0x0008u : 0x0010u,
            _ => down ? 0x0020u : 0x0040u,
        };
        Send(Mouse(0, 0, 0, flag));
    }

    public void Scroll(int dx, int dy)
    {
        if (dy != 0) Send(Mouse(0, 0, unchecked((uint)dy), MouseWheel));
        if (dx != 0) Send(Mouse(0, 0, unchecked((uint)dx), MouseHWheel));
    }

    public void Key(ushort vk, bool down)
    {
        var flags = (KeyMap.IsExtended(vk) ? KeyExtended : 0) | (down ? 0 : KeyUp);
        Send(Keyboard(vk, 0, flags));
    }

    public void Unicode(char c) =>
        Send(Keyboard(0, c, KeyUnicode), Keyboard(0, c, KeyUnicode | KeyUp));

    private static Input Mouse(int x, int y, uint data, uint flags) =>
        new() { Type = InputMouse, U = new InputUnion { Mouse = new MouseInput { X = x, Y = y, MouseData = data, Flags = flags } } };

    private static Input Keyboard(ushort vk, ushort scan, uint flags) =>
        new() { Type = InputKeyboard, U = new InputUnion { Keyboard = new KeybdInput { Vk = vk, Scan = scan, Flags = flags } } };

    private static void Send(params Input[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeybdInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput
    {
        public ushort Vk, Scan;
        public uint Flags, Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
