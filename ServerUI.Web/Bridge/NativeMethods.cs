using System;
using System.Runtime.InteropServices;

namespace ServerUI.Web.Bridge;

internal static class NativeMethods
{
    [DllImport("user32.dll")]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    internal const int GWL_EXSTYLE = -20;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_NOACTIVATE = 0x08000000;
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    internal static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool AttachConsole(uint dwProcessId);

    internal const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    internal const int WM_NCLBUTTONDOWN = 0x00A1;
    internal const int HTCAPTION = 2;

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const int DWMWA_BORDER_COLOR = 34;

    /// <summary>Win11 DWM 圆角 8px；系统不支持时静默返回 false（保持直角）。</summary>
    internal static bool TrySetRoundedCorners(IntPtr hwnd)
    {
        try
        {
            var pref = DWMWCP_ROUND;
            return DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把系统边框染成主题色（失败静默；窗口自身仍画 1px 边框）。</summary>
    internal static bool TrySetBorderColor(IntPtr hwnd, int bgr)
    {
        try
        {
            var color = bgr;
            return DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref color, sizeof(int)) == 0;
        }
        catch
        {
            return false;
        }
    }

    internal static int ColorRef(byte r, byte g, byte b) => (b << 16) | (g << 8) | r;
}
