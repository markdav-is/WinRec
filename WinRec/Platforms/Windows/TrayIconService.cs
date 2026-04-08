using System.Runtime.InteropServices;

namespace WinRec.Platforms.Windows;

/// <summary>
/// Minimal Win32 system-tray icon using Shell_NotifyIcon + SetWindowSubclass.
/// Usage:
///   1. Call <see cref="Create"/> once you have a valid HWND.
///   2. Subscribe to <see cref="ShowRequested"/> / <see cref="ExitRequested"/> /
///      <see cref="RecordRequested"/> / <see cref="StopRequested"/>.
///   3. Call <see cref="Dispose"/> when the app exits.
/// </summary>
internal sealed class TrayIconService : IDisposable
{
    // ──────────────────────────────────────────────────────────
    // Win32 constants
    // ──────────────────────────────────────────────────────────
    private const uint NIM_ADD    = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON    = 0x00000002;
    private const uint NIF_TIP     = 0x00000004;

    private const uint WM_USER            = 0x0400;
    private const uint WM_TRAYICON        = WM_USER + 1;
    private const uint WM_LBUTTONDBLCLK   = 0x0203;
    private const uint WM_RBUTTONUP       = 0x0205;

    private const uint WM_SYSCOMMAND      = 0x0112;
    private const uint SC_CLOSE           = 0xF060;
    private const uint SC_MINIMIZE        = 0xF020;

    private const uint MF_STRING          = 0x00000000;
    private const uint MF_SEPARATOR       = 0x00000800;
    private const uint MF_GRAYED          = 0x00000001;

    private const uint TPM_RIGHTBUTTON    = 0x0002;
    private const uint TPM_RETURNCMD      = 0x0100;

    private const uint IDI_APPLICATION    = 32512;

    private const uint MENU_SHOW   = 1001;
    private const uint MENU_RECORD = 1002;
    private const uint MENU_STOP   = 1003;
    private const uint MENU_EXIT   = 1004;

    // Subclass ID (arbitrary, just needs to be unique per window)
    private const uint SUBCLASS_ID = 42;

    // ──────────────────────────────────────────────────────────
    // P/Invoke declarations
    // ──────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int    cbSize;
        public IntPtr hWnd;
        public uint   uID;
        public uint   uFlags;
        public uint   uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint   dwState;
        public uint   dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint   uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint   dwInfoFlags;
        public Guid   guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpdata);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint fuFlags, int x, int y,
                                                 IntPtr hWnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass,
                                                  uint uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass,
                                                     uint uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg,
                                                  IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetFocus(IntPtr hWnd);

    private const int SW_RESTORE  = 9;
    private const int SW_HIDE     = 0;
    private const int SW_SHOW     = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg,
                                          IntPtr wParam, IntPtr lParam,
                                          IntPtr uIdSubclass, IntPtr dwRefData);

    // ──────────────────────────────────────────────────────────
    // Fields
    // ──────────────────────────────────────────────────────────
    private IntPtr _hwnd;
    private bool   _iconAdded;
    // Keep a GC root so the delegate is not collected while subclassing
    private readonly SubclassProc _subclassDelegate;

    // ──────────────────────────────────────────────────────────
    // Events
    // ──────────────────────────────────────────────────────────
    public event Action? ShowRequested;
    public event Action? ExitRequested;
    public event Action? RecordRequested;
    public event Action? StopRequested;

    // ──────────────────────────────────────────────────────────
    // Constructor / Create
    // ──────────────────────────────────────────────────────────
    public TrayIconService()
    {
        _subclassDelegate = SubclassCallback;
    }

    /// <summary>
    /// Creates and registers the system-tray icon.
    /// Must be called from the UI thread after the window is visible.
    /// </summary>
    public void Create(IntPtr hwnd)
    {
        _hwnd = hwnd;

        // Subclass the window so we can intercept WM_TRAYICON, WM_SYSCOMMAND, etc.
        SetWindowSubclass(_hwnd, _subclassDelegate, SUBCLASS_ID, IntPtr.Zero);

        var nid = BuildNid();
        nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        nid.uCallbackMessage = WM_TRAYICON;
        nid.hIcon = LoadIcon(IntPtr.Zero, new IntPtr(IDI_APPLICATION));
        nid.szTip = "WinRec – Audio Recorder";

        Shell_NotifyIcon(NIM_ADD, ref nid);
        _iconAdded = true;
    }

    /// <summary>Updates the tray icon tooltip to reflect recording state.</summary>
    public void UpdateTooltip(string tip)
    {
        if (!_iconAdded) return;
        var nid = BuildNid();
        nid.uFlags = NIF_TIP;
        nid.szTip  = tip.Length > 127 ? tip[..127] : tip;
        Shell_NotifyIcon(NIM_MODIFY, ref nid);
    }

    // ──────────────────────────────────────────────────────────
    // Window-subclass callback
    // ──────────────────────────────────────────────────────────
    private IntPtr SubclassCallback(IntPtr hWnd, uint uMsg,
                                    IntPtr wParam, IntPtr lParam,
                                    IntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == WM_TRAYICON)
        {
            uint mouseMsg = (uint)(lParam.ToInt64() & 0xFFFF);
            switch (mouseMsg)
            {
                case WM_LBUTTONDBLCLK:
                    ShowRequested?.Invoke();
                    return IntPtr.Zero;

                case WM_RBUTTONUP:
                    ShowContextMenu();
                    return IntPtr.Zero;
            }
        }
        else if (uMsg == WM_SYSCOMMAND)
        {
            uint cmd = (uint)(wParam.ToInt64() & 0xFFF0);
            if (cmd == SC_CLOSE || cmd == SC_MINIMIZE)
            {
                // Hide to tray instead of closing/minimising
                ShowWindow(_hwnd, SW_HIDE);
                return IntPtr.Zero;
            }
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    // ──────────────────────────────────────────────────────────
    // Context menu
    // ──────────────────────────────────────────────────────────
    private void ShowContextMenu()
    {
        IntPtr hMenu = CreatePopupMenu();
        try
        {
            AppendMenu(hMenu, MF_STRING,    MENU_SHOW,   "Open WinRec");
            AppendMenu(hMenu, MF_SEPARATOR, 0,           null);
            AppendMenu(hMenu, MF_STRING,    MENU_RECORD, "⏺  Start Recording");
            AppendMenu(hMenu, MF_STRING,    MENU_STOP,   "⏹  Stop Recording");
            AppendMenu(hMenu, MF_SEPARATOR, 0,           null);
            AppendMenu(hMenu, MF_STRING,    MENU_EXIT,   "Exit");

            GetCursorPos(out POINT pt);
            SetForegroundWindow(_hwnd);

            uint chosen = TrackPopupMenuEx(hMenu, TPM_RIGHTBUTTON | TPM_RETURNCMD,
                                           pt.X, pt.Y, _hwnd, IntPtr.Zero);
            switch (chosen)
            {
                case MENU_SHOW:   ShowRequested?.Invoke();   break;
                case MENU_RECORD: RecordRequested?.Invoke(); break;
                case MENU_STOP:   StopRequested?.Invoke();   break;
                case MENU_EXIT:   ExitRequested?.Invoke();   break;
            }
        }
        finally
        {
            DestroyMenu(hMenu);
        }
    }

    // ──────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────
    private NOTIFYICONDATA BuildNid() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd   = _hwnd,
        uID    = 1,
        szTip  = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty
    };

    // ──────────────────────────────────────────────────────────
    // Dispose
    // ──────────────────────────────────────────────────────────
    public void Dispose()
    {
        if (_iconAdded)
        {
            var nid = BuildNid();
            Shell_NotifyIcon(NIM_DELETE, ref nid);
            _iconAdded = false;
        }

        if (_hwnd != IntPtr.Zero)
        {
            RemoveWindowSubclass(_hwnd, _subclassDelegate, SUBCLASS_ID);
            _hwnd = IntPtr.Zero;
        }
    }
}
