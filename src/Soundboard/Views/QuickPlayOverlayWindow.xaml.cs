using System.Runtime.InteropServices;
using System.Windows;
using Soundboard.ViewModels;

namespace Soundboard.Views;

/// <summary>A borderless, topmost, taskbar-hidden popup toggled by a global hotkey — lets the
/// user play a sound without alt-tabbing to the main window. Registered as a DI singleton and
/// shown/hidden rather than recreated per toggle, so its sound list stays warm and it doesn't
/// re-subscribe to LibraryChanged on every hotkey press (see MainViewModel's overlay-toggle
/// handler for the show/hide logic).</summary>
public partial class QuickPlayOverlayWindow : Window
{
    private readonly QuickPlayOverlayViewModel _viewModel;
    private LowLevelMouseProc? _mouseProc;
    private IntPtr _mouseHookHandle = IntPtr.Zero;

    public QuickPlayOverlayWindow(QuickPlayOverlayViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        _viewModel.RequestClose += (_, _) => Hide();

        // Single place that reacts to every Show()/Hide() regardless of which code path
        // triggered it (RequestClose, the dismiss hook below, direct calls), rather than
        // scattering hook install/uninstall calls at each call site.
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) InstallDismissHook();
            else UninstallDismissHook();
        };
    }

    /// <summary>Positions the window at the current cursor location, clamped so it can never
    /// open partially off-screen, refreshes its sound list, then shows and focuses it.</summary>
    public void ShowNearCursor()
    {
        _viewModel.Refresh();

        // Measure first so Height reflects actual content before it's used for clamping —
        // SizeToContent="Height" means Height isn't accurate until a layout pass has happened.
        Show();
        UpdateLayout();

        var cursor = GetCursorPosInDips();
        var workArea = SystemParameters.WorkArea;

        Left = Math.Min(cursor.X, workArea.Right - Width);
        Top = Math.Min(cursor.Y, workArea.Bottom - Height);
        Left = Math.Max(Left, workArea.Left);
        Top = Math.Max(Top, workArea.Top);

        ForceToFront();

        Activate();
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>Topmost="True" in XAML only puts this window in the "always on top" band — it
    /// doesn't guarantee it's in FRONT of every other topmost window already in that band (other
    /// apps' own always-on-top overlays, etc.), and if something else grabs topmost status after
    /// this window was created, WPF doesn't automatically re-assert it. Toggling Topmost off/on
    /// is the standard trick to force it back to the very front of the topmost band, and the raw
    /// SetWindowPos call backs that up at the Win32 level rather than relying solely on WPF's own
    /// property plumbing. Called every time the overlay is shown, not just once at construction.
    ///
    /// Honest limit: nothing here can appear over a game running in true DirectX/OpenGL exclusive
    /// fullscreen — that mode bypasses the desktop compositor entirely, which is an OS/GPU-level
    /// wall no ordinary window (this one included) can cross. It works over regular windows and
    /// the borderless/"windowed fullscreen" mode most modern games default to.</summary>
    private void ForceToFront()
    {
        Topmost = false;
        Topmost = true;

        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
    }

    // --- Click-outside-to-dismiss, via a scoped low-level mouse hook ---
    //
    // Originally used the Deactivated event, which turned out to be the actual cause of a
    // reported bug: over a fullscreen game, the game can reclaim foreground activation the
    // instant ANY click happens — including a click ON this window's own buttons — racing
    // ahead of the click's routed event. Deactivated fired first, hid the window, and ate the
    // click before PlayAndDismissCommand ever ran. Checking the click's raw screen position
    // against this window's own bounds is independent of window activation entirely, so it
    // can't be raced by whatever the game underneath does with focus.

    private void InstallDismissHook()
    {
        if (_mouseHookHandle != IntPtr.Zero) return;

        _mouseProc = MouseHookCallback; // kept alive as a field so the GC can't collect the delegate mid-hook
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        _mouseHookHandle = SetWindowsHookEx(WhMouseLl, _mouseProc, GetModuleHandle(curModule.ModuleName), 0);
    }

    private void UninstallDismissHook()
    {
        if (_mouseHookHandle == IntPtr.Zero) return;

        UnhookWindowsHookEx(_mouseHookHandle);
        _mouseHookHandle = IntPtr.Zero;
        _mouseProc = null;
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WmLButtonDown || wParam == (IntPtr)WmRButtonDown))
        {
            // Marshal.PtrToStructure is plain memory reading, safe on any thread — but a
            // low-level hook callback isn't guaranteed to run on the thread that installed it
            // (it wasn't here — Window.Left/ActualWidth/Hide() all threw "the calling thread
            // cannot access this object because a different thread owns it" without this).
            // Everything that touches this WPF window has to go through the Dispatcher.
            var hookStruct = Marshal.PtrToStructure<MsllHookStruct>(lParam);
            var screenX = hookStruct.pt.X;
            var screenY = hookStruct.pt.Y;

            Dispatcher.BeginInvoke(() =>
            {
                if (!GetScreenBoundsInPhysicalPixels().Contains(screenX, screenY))
                {
                    Hide();
                }
            });
        }

        return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
    }

    /// <summary>MSLLHOOKSTRUCT coordinates are physical screen pixels, but Left/Top/ActualWidth/
    /// ActualHeight are device-independent units — converted here the same way GetCursorPosInDips
    /// converts the other direction, so the bounds check is correct at any display scaling.</summary>
    private Rect GetScreenBoundsInPhysicalPixels()
    {
        var topLeft = new Point(Left, Top);
        var bottomRight = new Point(Left + ActualWidth, Top + ActualHeight);

        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice;
        if (transform is { } m)
        {
            topLeft = m.Transform(topLeft);
            bottomRight = m.Transform(bottomRight);
        }

        return new Rect(topLeft, bottomRight);
    }

    private const int WhMouseLl = 14;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MsllHookStruct
    {
        public Win32Point pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    /// <summary>GetCursorPos returns physical device pixels, but Window.Left/Top expect
    /// device-independent units — the two only match at exactly 100% display scaling. Anything
    /// else (125%, 150%, etc., the actual default on most Windows displays) would silently
    /// place the overlay away from the real cursor position without this conversion.</summary>
    private Point GetCursorPosInDips()
    {
        GetCursorPos(out var point);
        var physicalPoint = new Point(point.X, point.Y);

        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
        return transform is { } m ? m.Transform(physicalPoint) : physicalPoint;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Win32Point point);

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Point
    {
        public int X;
        public int Y;
    }
}
