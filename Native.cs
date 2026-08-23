using System.Runtime.InteropServices;

namespace SelectAndRead;

/// <summary>All P/Invoke declarations, kept in one place (SPEC 11).</summary>
internal static class Native
{
    // --- Primary screen metrics -------------------------------------------------
    // Under Per-Monitor-V2 awareness these return true physical pixels, which is the
    // single coordinate space the whole app works in (SPEC 4.1). The app supports the
    // primary monitor only, so the virtual-desktop metrics are deliberately not used.
    internal const int SM_CXSCREEN = 0;
    internal const int SM_CYSCREEN = 1;

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int nIndex);

    // --- Cursor position --------------------------------------------------------
    // Used to seed the overlay's crosshair, since the mouse may never move between the
    // overlay opening and the click. Raw physical pixels under Per-Monitor-V2, with no
    // WinForms layer that could reintroduce a second coordinate space (SPEC 4.1 - 4.2).
    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        internal int X;
        internal int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    // --- Window positioning -----------------------------------------------------
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    // --- Window regions ---------------------------------------------------------
    // Used to cut the middle out of the highlight window, so the window is only its own
    // border and has no pixels at all over the word it marks (SPEC 16.4). Shaping the window
    // rather than painting transparently is what makes it click-through for free: the
    // removed area is not part of the window, so it cannot be hit-tested or drawn over.
    internal const int RGN_OR = 2;
    internal const int RGN_DIFF = 4;

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);

    [DllImport("gdi32.dll")]
    internal static extern int CombineRgn(IntPtr dest, IntPtr src1, IntPtr src2, int mode);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(IntPtr handle);

    /// <summary>
    /// The window takes ownership of the region on success, so the handle must not be
    /// deleted afterwards - only one that was never handed over.
    /// </summary>
    [DllImport("user32.dll")]
    internal static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    // Reading the shape back, for --highlight-metrics. GetWindowRgn copies into a region
    // the caller already owns rather than returning one, hence the empty region first.
    [DllImport("user32.dll")]
    internal static extern int GetWindowRgn(IntPtr hWnd, IntPtr hRgn);

    [DllImport("gdi32.dll")]
    internal static extern int GetRgnBox(IntPtr hRgn, out RECT lprc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PtInRegion(IntPtr hRgn, int x, int y);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    // --- Global hotkeys ---------------------------------------------------------
    internal const uint MOD_ALT = 0x0001;
    internal const uint MOD_CONTROL = 0x0002;
    internal const uint MOD_SHIFT = 0x0004;
    internal const uint MOD_WIN = 0x0008;
    /// <summary>Stops key-repeat from re-firing the hotkey while it is held down.</summary>
    internal const uint MOD_NOREPEAT = 0x4000;

    internal const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // --- Low-level keyboard hook ------------------------------------------------
    internal const int WH_KEYBOARD_LL = 13;
    internal const int HC_ACTION = 0;
    internal const int WM_KEYDOWN = 0x0100;
    internal const int WM_SYSKEYDOWN = 0x0104;

    [StructLayout(LayoutKind.Sequential)]
    internal struct KBDLLHOOKSTRUCT
    {
        internal uint vkCode;
        internal uint scanCode;
        internal uint flags;
        internal uint time;
        internal IntPtr dwExtraInfo;
    }

    internal delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookExW(
        int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandleW(string? lpModuleName);

    // --- Screen capture ---------------------------------------------------------
    // Raw BitBlt rather than Graphics.CopyFromScreen, because CopyPixelOperation is not a
    // [Flags] enum: OR-ing CaptureBlt into SourceCopy yields an undefined value that the
    // managed overload rejects outright at runtime.
    internal const uint SRCCOPY = 0x00CC0020;
    internal const uint CAPTUREBLT = 0x40000000;

    [DllImport("user32.dll")]
    internal static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BitBlt(
        IntPtr hdcDest, int xDest, int yDest, int width, int height,
        IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    // --- Icon cleanup -----------------------------------------------------------
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    // --- Console attach, for the debug CLI modes (SPEC 12.2) --------------------
    // The app is a WinExe and so has no console of its own; this borrows the parent's.
    internal const int ATTACH_PARENT_PROCESS = -1;
    internal const int STD_OUTPUT_HANDLE = -11;
    internal static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GetStdHandle(int nStdHandle);
}
