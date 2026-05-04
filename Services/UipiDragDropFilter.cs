using System;
using System.Runtime.InteropServices;

namespace GenshinLyrePlayer.Services;

/// <summary>
/// 由于 <see cref="app.manifest"/> 里把本程序声明为 <c>requireAdministrator</c>，
/// Windows 的 UIPI（User Interface Privilege Isolation）默认会阻止低完整性级别的进程
/// （比如普通用户身份运行的资源管理器 explorer.exe）向高完整性级别窗口发送某些消息——
/// 其中就包括拖放需要用到的：
/// <list type="bullet">
///   <item><c>WM_DROPFILES (0x0233)</c> —— 拖放文件落点消息；</item>
///   <item><c>WM_COPYDATA (0x004A)</c> —— 拖放数据传输；</item>
///   <item><c>0x0049</c> (<c>WM_COPYGLOBALDATA</c>) —— 跨进程剪贴板/拖放共享内存。</item>
/// </list>
/// 不解除这些消息的过滤，从资源管理器拖文件到本窗口时鼠标会一直显示"禁止"图标，
/// 我们的 <c>DragOver</c> 事件根本不会被触发。
///
/// <para>
/// 解决方法是调用 <c>ChangeWindowMessageFilterEx</c> 把这三条消息显式加入白名单。
/// 该 API 自 Windows 7 起可用；在非管理员或非 Windows 平台上调用失败也无所谓——
/// 普通完整性级别的进程本来就不受 UIPI 限制，拖拽会照常工作。
/// </para>
/// </summary>
internal static class UipiDragDropFilter
{
    private const uint WM_DROPFILES = 0x0233;
    private const uint WM_COPYDATA = 0x004A;
    private const uint WM_COPYGLOBALDATA = 0x0049;

    private const uint MSGFLT_ALLOW = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct CHANGEFILTERSTRUCT
    {
        public uint cbSize;
        public uint ExtStatus;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ChangeWindowMessageFilterEx(
        IntPtr hWnd, uint message, uint action, ref CHANGEFILTERSTRUCT pChangeFilterStruct);

    /// <summary>
    /// 把拖放相关的 3 条 Windows 消息显式加入指定窗口的接收白名单。
    /// 仅在 Windows 上有效；其他平台和老系统上调用失败时静默忽略。
    /// </summary>
    public static void Allow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var cfs = new CHANGEFILTERSTRUCT { cbSize = (uint)Marshal.SizeOf<CHANGEFILTERSTRUCT>() };
        try
        {
            ChangeWindowMessageFilterEx(hWnd, WM_DROPFILES, MSGFLT_ALLOW, ref cfs);
            ChangeWindowMessageFilterEx(hWnd, WM_COPYDATA, MSGFLT_ALLOW, ref cfs);
            ChangeWindowMessageFilterEx(hWnd, WM_COPYGLOBALDATA, MSGFLT_ALLOW, ref cfs);
        }
        catch
        {
            // 老 Windows / 非 Windows 平台调用失败不影响主流程，普通权限下本来也不需要。
        }
    }
}
