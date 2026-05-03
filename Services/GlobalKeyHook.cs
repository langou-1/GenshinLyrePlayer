using System;
using System.Runtime.InteropServices;

namespace GenshinLyrePlayer.Services;

/// <summary>
/// 基于 WH_KEYBOARD_LL 的全局键盘钩子。
/// 用途：即使当前焦点在原神窗口，也能监听到我们的热键（如 F8 播放/暂停、F9 停止），
/// 从而解决"点击播放按钮后焦点仍在本程序、无法触发演奏"的问题。
///
/// 注意：
///  1. 钩子只做"观察"，不会阻塞或修改系统中的任何按键；
///  2. 我们通过 SendInput 注入给原神的按键会带有 LLKHF_INJECTED 标志，
///     这里会主动过滤，避免自己触发自己。
/// </summary>
public static class GlobalKeyHook
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const uint LLKHF_INJECTED = 0x00000010;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private static IntPtr _hook;
    private static LowLevelKeyboardProc? _proc;   // 必须保持引用以防被 GC 回收

    /// <summary>全局按键按下事件。参数为虚拟键码 vkCode。</summary>
    public static event Action<uint>? KeyDown;

    public static bool Install()
    {
        if (_hook != IntPtr.Zero) return true;
        _proc = HookProc;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        return _hook != IntPtr.Zero;
    }

    public static void Uninstall()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _proc = null;
    }

    private static IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
            {
                try
                {
                    var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    // 过滤掉自己通过 SendInput 注入的按键
                    if ((k.flags & LLKHF_INJECTED) == 0)
                    {
                        KeyDown?.Invoke(k.vkCode);
                    }
                }
                catch { /* 钩子里绝不能抛出异常 */ }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
