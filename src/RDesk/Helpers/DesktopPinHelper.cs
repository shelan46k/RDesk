using System.Runtime.InteropServices;
using System.Text;

namespace RDesk.Helpers;

/// <summary>
/// Desktop-layer pinning (same approach as DeskBox's DesktopPinned mode):
/// the window is owned by Explorer's SHELLDLL_DefView, so "Show desktop" (Win+D)
/// does not minimize it; it simply sinks behind other apps when they are activated.
/// </summary>
public static class DesktopPinHelper
{
    private const int GWL_EXSTYLE = -20;
    private const int GWLP_HWNDPARENT = -8;
    private const int WS_EX_APPWINDOW = 0x00040000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private static readonly nint HWND_TOP = 0;
    private static readonly nint HWND_TOPMOST = -1;
    private static readonly nint HWND_NOTOPMOST = -2;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const uint SWP_ASYNCWINDOWPOS = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    /// <summary>
    /// Moves / sizes a desktop-owned HWND without waiting for Explorer (the owner).
    /// AppWindow.Move / MoveAndResize can hang or swallow mouse-up when the owner is SHELLDLL_DefView.
    /// </summary>
    public static void SetBounds(nint hWnd, int x, int y, int width, int height)
    {
        if (hWnd == nint.Zero)
        {
            return;
        }

        SetWindowPos(hWnd, nint.Zero, x, y, width, height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_ASYNCWINDOWPOS);
    }

    public static void Move(nint hWnd, int x, int y)
    {
        if (hWnd == nint.Zero)
        {
            return;
        }

        SetWindowPos(hWnd, nint.Zero, x, y, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_ASYNCWINDOWPOS);
    }

    public static bool TryGetBounds(nint hWnd, out NativeRect rect)
    {
        rect = default;
        return hWnd != nint.Zero && GetWindowRect(hWnd, out rect);
    }

    public static void ApplyToolWindowStyle(nint hWnd)
    {
        if (hWnd == nint.Zero)
        {
            return;
        }

        var ex = GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
        ex |= WS_EX_TOOLWINDOW;
        ex &= ~WS_EX_APPWINDOW;
        SetWindowLongPtr(hWnd, GWL_EXSTYLE, (nint)ex);
        SetWindowPos(hWnd, nint.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED | 0x0004);
    }

    /// <returns>True when the window is attached to the desktop layer.</returns>
    public static bool SetPinToDesktop(nint hWnd, bool isPinned)
    {
        if (hWnd == nint.Zero)
        {
            return false;
        }

        ApplyToolWindowStyle(hWnd);

        if (!isPinned)
        {
            SetWindowLongPtr(hWnd, GWLP_HWNDPARENT, nint.Zero);
            return false;
        }

        var defView = FindExistingDefView();
        if (defView == nint.Zero)
        {
            return false;
        }

        if (GetWindowLongPtr(hWnd, GWLP_HWNDPARENT) != defView)
        {
            SetWindowLongPtr(hWnd, GWLP_HWNDPARENT, defView);
        }

        return GetWindowLongPtr(hWnd, GWLP_HWNDPARENT) == defView;
    }

    /// <summary>
    /// Lets DWM clip the window to Windows 11 rounded corners (anti-aliased, backdrop included),
    /// so the acrylic never shows as square "residue" outside a XAML CornerRadius.
    /// </summary>
    public static void ApplyRoundedCorners(nint hWnd, bool small = false)
    {
        if (hWnd == nint.Zero)
        {
            return;
        }

        var preference = small ? DWMWCP_ROUNDSMALL : DWMWCP_ROUND;
        DwmSetWindowAttribute(hWnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

    /// <summary>
    /// Lets the compositor draw the desktop through the client area so acrylic / clear fills work.
    /// </summary>
    public static void ExtendGlassFrame(nint hWnd)
    {
        if (hWnd == nint.Zero)
        {
            return;
        }

        var margins = new Margins { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
        DwmExtendFrameIntoClientArea(hWnd, ref margins);
    }

    /// <summary>Thin DWM border tinted to match the theme (Windows 11 only; ignored elsewhere).</summary>
    public static void SetBorderColor(nint hWnd, Windows.UI.Color color)
    {
        if (hWnd == nint.Zero)
        {
            return;
        }

        var colorRef = color.R | (color.G << 8) | (color.B << 16);
        DwmSetWindowAttribute(hWnd, DWMWA_BORDER_COLOR, ref colorRef, sizeof(int));
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_ROUND = 2;
    private const int DWMWCP_ROUNDSMALL = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins pMarInset);

    /// <summary>Temporarily raise above other windows (tray / hotkey reveal).</summary>
    public static void BringToTop(nint hWnd)
    {
        if (hWnd == nint.Zero)
        {
            return;
        }

        SetWindowPos(hWnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
        SetForegroundWindow(hWnd);
    }

    /// <summary>
    /// Pop a desktop-owned box above other apps. Explorer as owner would otherwise keep it
    /// at the bottom; detach, pulse TOPMOST, then leave it in the normal z-order.
    /// Call <see cref="SetPinToDesktop"/> later to put it back on the desktop layer.
    /// </summary>
    public static void RaiseAboveApps(nint hWnd)
    {
        if (hWnd == nint.Zero)
        {
            return;
        }

        SetWindowLongPtr(hWnd, GWLP_HWNDPARENT, nint.Zero);
        SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOOWNERZORDER);
        SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOOWNERZORDER);
        SetForegroundWindow(hWnd);
    }

    /// <summary>
    /// Only uses a SHELLDLL_DefView Explorer already created; never forces WorkerW creation
    /// (that can scramble desktop icon positions during login).
    /// </summary>
    private static nint FindExistingDefView()
    {
        var progman = FindWindow("Progman", null);
        var defView = progman != nint.Zero ? FindWindowEx(progman, nint.Zero, "SHELLDLL_DefView", null) : nint.Zero;
        if (defView != nint.Zero)
        {
            return defView;
        }

        nint found = nint.Zero;
        EnumWindows((top, _) =>
        {
            var cls = new StringBuilder(64);
            GetClassName(top, cls, cls.Capacity);
            if (cls.ToString() != "WorkerW")
            {
                return true;
            }

            var child = FindWindowEx(top, nint.Zero, "SHELLDLL_DefView", null);
            if (child != nint.Zero)
            {
                found = child;
                return false;
            }

            return true;
        }, nint.Zero);
        return found;
    }

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint hwndParent, nint hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out NativeRect lpRect);
}
