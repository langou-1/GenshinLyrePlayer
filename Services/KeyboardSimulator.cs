using System;
using System.Runtime.InteropServices;

namespace GenshinLyrePlayer.Services;

/// <summary>
/// 基于 Win32 SendInput 的键盘模拟器。使用扫描码 (scan code) 发送，
/// 以便在绝大多数通过 DirectInput / Raw Input 读取键盘的游戏（含原神）中生效。
/// </summary>
public static class KeyboardSimulator
{
    #region Win32

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
        public static int Size => Marshal.SizeOf<INPUT>();
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    private const uint MAPVK_VK_TO_VSC = 0x00;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    #endregion

    /// <summary>将 A–Z 字母转换为扫描码。</summary>
    public static ushort GetScanCode(char letter)
    {
        uint vk = char.ToUpperInvariant(letter); // 'A'..'Z' 对应 VK 0x41..0x5A
        return (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC);
    }

    public static void Press(char letter) => SendKey(letter, down: true);
    public static void Release(char letter) => SendKey(letter, down: false);

    /// <summary>瞬时按下再抬起（阻塞调用者线程极短时间）。</summary>
    public static void Tap(char letter, int holdMs = 15)
    {
        SendKey(letter, down: true);
        if (holdMs > 0) System.Threading.Thread.Sleep(holdMs);
        SendKey(letter, down: false);
    }

    public static void SendKey(char letter, bool down)
    {
        ushort scan = GetScanCode(letter);
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = scan,
                    dwFlags = KEYEVENTF_SCANCODE | (down ? 0u : KEYEVENTF_KEYUP),
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                }
            }
        };
        var arr = new[] { input };
        SendInput(1, arr, INPUT.Size);
    }
}
