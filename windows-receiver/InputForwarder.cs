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
    readonly Func<bool> _healthy;   // 控制通道健康？失联时不吞点击（放行给本机）
    readonly Action<byte[]> _send;
    readonly int _w, _h;
    IntPtr _mouseHook, _kbdHook;
    readonly NativeMethods.LowLevelMouseProc _mouseProc;
    readonly NativeMethods.LowLevelKeyboardProc _kbdProc;
    readonly ConcurrentQueue<byte[]> _queue = new();
    readonly AutoResetEvent _signal = new(false);
    volatile bool _running = true;
    readonly Thread _sender;
    private long _lastMoveSentTicks;   // 最后一次鼠标移动的发送时刻（输入延迟估算用）

    // 本地光标位置（钩子频率更新）：绘制光标用，零往返延迟
    private readonly object _localLock = new();
    private float _localNx, _localNy;
    private bool _localValid;

    /// 本地鼠标位置（相对屏幕归一化）。仅在钩子活跃（控制中）时有效
    public bool TryGetLocalCursor(out float nx, out float ny)
    {
        lock (_localLock) { nx = _localNx; ny = _localNy; return _localValid; }
    }

    /// 最后一次移动的发送时刻（Ticks）；0 = 尚无移动
    public long LastMoveSentTicks => Interlocked.Read(ref _lastMoveSentTicks);

    public InputForwarder(Func<bool> isActive, Action<byte[]> send, int w, int h, Func<bool>? healthy = null)
    {
        _isActive = isActive; _send = send; _w = w; _h = h;
        _healthy = healthy ?? (() => true);
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
            int msg = wParam.ToInt32();
            var info = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            float nx = (float)info.pt.X / _w, ny = (float)info.pt.Y / _h;
            switch (wParam.ToInt32())
            {
                case WM_MOUSEMOVE:
                    Interlocked.Exchange(ref _lastMoveSentTicks, DateTime.UtcNow.Ticks);
                    lock (_localLock) { _localNx = nx; _localNy = ny; _localValid = true; }
                    Enqueue(Move(nx, ny));
                    // 移动放行：让系统光标自然移动（它就是光标层）；点击/滚轮仍被吞掉
                    return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
                case WM_LBUTTONDOWN:
                case WM_LBUTTONUP:
                case WM_RBUTTONDOWN:
                case WM_RBUTTONUP:
                case WM_MBUTTONDOWN:
                case WM_MBUTTONUP:
                    byte btn = 0; bool isDown = msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN;
                    if (msg is WM_RBUTTONDOWN or WM_RBUTTONUP) btn = 1;
                    if (msg is WM_MBUTTONDOWN or WM_MBUTTONUP) btn = 2;
                    if (!_healthy()) return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);   // 失联：放行给本机
                    Enqueue(Button(btn, isDown, nx, ny));
                    return (IntPtr)1;
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
                // Esc (0x1B) 退全屏，F1 (0x70) 显隐连接信息，F11 (0x7A) 切换全屏，放行给本机
                if (info.vkCode is 0x1B or 0x70 or 0x7A) return NativeMethods.CallNextHookEx(_kbdHook, nCode, wParam, lParam);
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
