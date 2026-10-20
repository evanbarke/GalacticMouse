using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GalacticMouse;

internal sealed class NativeInput : IDisposable
{
    private const int WhMouseLl = 14, WhKeyboardLl = 13, WmInput = 0x00FF;
    private const uint RidInput = 0x10000003, RidevInputSink = 0x00000100;
    private const uint InjectedMouse = 0x00000001, InjectedKey = 0x00000010;
    private readonly HookProc _mouseProc, _keyProc;
    private IntPtr _mouseHook, _keyHook;

    public Func<int, MouseHookData, bool>? MouseEvent;
    public Func<int, KeyHookData, bool>? KeyEvent;
    public Action<int, int>? RawMouseMove;

    public NativeInput(IntPtr window)
    {
        _mouseProc = MouseCallback;
        _keyProc = KeyCallback;
        var module = GetModuleHandle(null);
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, module, 0);
        _keyHook = SetWindowsHookEx(WhKeyboardLl, _keyProc, module, 0);
        if (_mouseHook == IntPtr.Zero || _keyHook == IntPtr.Zero)
        {
            Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not install input hooks");
        }
        var devices = new[] { new RAWINPUTDEVICE { UsagePage = 1, Usage = 2, Flags = RidevInputSink, Target = window } };
        if (!RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not register raw mouse input");
        }
    }

    private IntPtr MouseCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<MouseHookData>(lParam);
            if ((data.Flags & InjectedMouse) == 0 && MouseEvent?.Invoke(wParam.ToInt32(), data) == true)
                return new IntPtr(1);
        }
        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private IntPtr KeyCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<KeyHookData>(lParam);
            if ((data.Flags & InjectedKey) == 0 && KeyEvent?.Invoke(wParam.ToInt32(), data) == true)
                return new IntPtr(1);
        }
        return CallNextHookEx(_keyHook, code, wParam, lParam);
    }

    public void HandleMessage(ref Message m)
    {
        if (m.Msg != WmInput || RawMouseMove is null) return;
        uint size = 0;
        GetRawInputData(m.LParam, RidInput, null, ref size, 24);
        if (size < 48 || size > 4096) return;
        var data = new byte[size];
        if (GetRawInputData(m.LParam, RidInput, data, ref size, 24) != size) return;
        if (BitConverter.ToUInt32(data, 0) != 0) return; // RIM_TYPEMOUSE
        var flags = BitConverter.ToUInt16(data, 24);
        if ((flags & 1) != 0) return; // relative movement only
        RawMouseMove(BitConverter.ToInt32(data, 36), BitConverter.ToInt32(data, 40));
    }

    public static Point Cursor
    {
        get { GetCursorPos(out var point); return point; }
        set => SetCursorPos(value.X, value.Y);
    }

    public static bool IsPressed(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public static void InjectKey(uint scan, bool extended, bool up)
    {
        var flags = 0x0008u | (extended ? 0x0001u : 0u) | (up ? 0x0002u : 0u);
        var input = new INPUT { Type = 1, Ki = new KEYBDINPUT { Scan = (ushort)scan, Flags = flags } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    public static void InjectMouse(int message, int wheel = 0)
    {
        uint flags = message switch
        {
            0x0201 => 0x0002, 0x0202 => 0x0004,
            0x0204 => 0x0008, 0x0205 => 0x0010,
            0x0207 => 0x0020, 0x0208 => 0x0040,
            0x020B => 0x0080, 0x020C => 0x0100,
            0x020A => 0x0800, 0x020E => 0x1000,
            _ => 0
        };
        if (flags == 0) return;
        var input = new INPUT { Type = 0, Mi = new MOUSEINPUT { Data = unchecked((uint)wheel), Flags = flags } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    public void Dispose()
    {
        if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        if (_keyHook != IntPtr.Zero) { UnhookWindowsHookEx(_keyHook); _keyHook = IntPtr.Zero; }
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] public struct MouseHookData
    {
        public POINT Point; public uint MouseData, Flags, Time; public IntPtr Extra;
    }
    [StructLayout(LayoutKind.Sequential)] public struct KeyHookData
    {
        public uint Vk, Scan, Flags, Time; public IntPtr Extra;
    }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; public static implicit operator Point(POINT p) => new(p.X, p.Y); }
    [StructLayout(LayoutKind.Sequential)] private struct RAWINPUTDEVICE
    {
        public ushort UsagePage, Usage; public uint Flags; public IntPtr Target;
    }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct INPUT
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(8)] public MOUSEINPUT Mi;
        [FieldOffset(8)] public KEYBDINPUT Ki;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT
    {
        public int Dx, Dy; public uint Data, Flags, Time; public IntPtr Extra;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT
    {
        public ushort Vk, Scan; public uint Flags, Time; public IntPtr Extra;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputData(IntPtr raw, uint command, [Out] byte[]? data, ref uint size, uint headerSize);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
