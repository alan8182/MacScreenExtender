using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace MacScreenReceiver;

/// 全屏时用低级鼠标/键盘钩子捕获输入并转发到 Mac。
/// 钩子回调绝不能阻塞（系统会摘除钩子）：所有包先进内存队列，由专用线程发送。
public sealed class InputForwarder : IDisposable
{
    const int WH_MOUSE_LL = 14, WH_KEYBOARD_LL = 13;
    const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202,
              WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205,
              WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208, WM_MOUSEWHEEL = 0x20A;
    const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;

    readonly Func<bool> _isActive;
    readonly Action<byte[]> _send;
    readonly int _w, _h;
    IntPtr _mouseHook, _kbdHook;
    readonly NativeMethods.LowLevelMouseProc _mouseProc;
    readonly NativeMethods.LowLevelKeyboardProc _kbdProc;
    readonly ConcurrentQueue<byte[]> _queue = new();
    readonly AutoResetEvent _signal = new(false);
    volatile bool _running = true;
    readonly Thread _sender;

    public InputForwarder(Func<bool> isActive, Action<byte[]> send, int w, int h)
    {
        _isActive = isActive; _send = send; _w = w; _h = h;
        _mouseProc = MouseHookProc;
        _kbdProc = KbdHookProc;
        _mouseHook = NativeMethods.SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, NativeMethods.GetModuleHandle(null), 0);
        _kbdHook = NativeMethods.SetWindowsHookEx(WH_KEYBOARD_LL, _kbdProc, NativeMethods.GetModuleHandle(null), 0);
        _sender = new Thread(SenderLoop) { IsBackground = true, Name = "input-sender" };
        _sender.Start();
    }

    IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _isActive())
        {
            var info = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            float nx = (float)info.pt.X / _w, ny = (float)info.pt.Y / _h;
            switch (wParam.ToInt32())
            {
                case WM_MOUSEMOVE: Enqueue(Move(nx, ny)); return (IntPtr)1;
                case WM_LBUTTONDOWN: Enqueue(Button(0, true, nx, ny)); return (IntPtr)1;
                case WM_LBUTTONUP: Enqueue(Button(0, false, nx, ny)); return (IntPtr)1;
                case WM_RBUTTONDOWN: Enqueue(Button(1, true, nx, ny)); return (IntPtr)1;
                case WM_RBUTTONUP: Enqueue(Button(1, false, nx, ny)); return (IntPtr)1;
                case WM_MBUTTONDOWN: Enqueue(Button(2, true, nx, ny)); return (IntPtr)1;
                case WM_MBUTTONUP: Enqueue(Button(2, false, nx, ny)); return (IntPtr)1;
                case WM_MOUSEWHEEL:
                    short delta = (short)((info.mouseData >> 16) & 0xFFFF);
                    Enqueue(Wheel(0, delta / 120f));
                    return (IntPtr)1;
            }
        }
        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    IntPtr KbdHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _isActive())
        {
            int msg = wParam.ToInt32();
            bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;
            if (down || up)
            {
                var info = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                // Esc 留给本机（退出全屏）；Mac 侧需要 Esc 时先退全屏再按
                if (info.vkCode == 0x1B) return NativeMethods.CallNextHookEx(_kbdHook, nCode, wParam, lParam);
                _queue.Enqueue(Key((ushort)info.vkCode, down));
                return (IntPtr)1;   // 吞掉：Windows 不再响应（含 Alt+Tab/Win 键），全部交给 Mac
            }
        }
        return NativeMethods.CallNextHookEx(_kbdHook, nCode, wParam, lParam);
    }

    void Enqueue(byte[] p)
    {
        _queue.Enqueue(p);
        _signal.Set();
    }

    void SenderLoop()
    {
        while (_running)
        {
            while (_queue.TryDequeue(out var p))
            {
                try { _send(p); } catch { _ctlDead = true; }
                if (_ctlDead) return;   // 发送失败（链路断），钩子放行全部输入
            }
            _signal.WaitOne(50);
        }
    }
    private volatile bool _ctlDead;

    // 16 字节定长包：type u8@0, 参数见协议
    public static byte[] Move(float nx, float ny) { var p = new byte[16]; p[0] = 1; Write(p, 4, nx); Write(p, 8, ny); return p; }
    public static byte[] Button(byte b, bool down, float nx, float ny) { var p = new byte[16]; p[0] = (byte)(down ? 2 : 3); p[4] = b; Write(p, 4, nx); Write(p, 8, ny); return p; }
    public static byte[] Wheel(float dx, float dy) { var p = new byte[16]; p[0] = 4; Write(p, 4, dx); Write(p, 8, dy); return p; }
    public static byte[] Key(ushort code, bool down) { var p = new byte[16]; p[0] = 5; p[4] = (byte)(down ? 1 : 0); p[6] = (byte)(code & 0xFF); p[7] = (byte)(code >> 8); return p; }

    static void Write(byte[] p, int off, float v) { var b = BitConverter.GetBytes(v); Array.Copy(b, 0, p, off, 4); }

    public void Dispose()
    {
        _running = false;
        _signal.Set();
        if (_mouseHook != IntPtr.Zero) { NativeMethods.UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        if (_kbdHook != IntPtr.Zero) { NativeMethods.UnhookWindowsHookEx(_kbdHook); _kbdHook = IntPtr.Zero; }
    }
}

static class NativeMethods
{
    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string lpModuleName);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
    public const uint GA_ROOT = 2;
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData; public uint flags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] public struct KBDLLHOOKSTRUCT { public uint vkCode; public uint scanCode; public uint flags; public uint time; public IntPtr dwExtraInfo; }
}
