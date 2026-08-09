using System.Runtime.InteropServices;

namespace SelectAndRead;

/// <summary>
/// Watches for ESC while speech is playing (SPEC 8.2).
///
/// Two properties are deliberate and important. The hook is installed only for the
/// duration of playback, not for the life of the process; and it always calls
/// CallNextHookEx, so it observes ESC without consuming it and the foreground application
/// still receives its own key. Registering ESC as a global hotkey instead would break
/// every dialog on the system for as long as the app happened to be reading.
/// </summary>
internal sealed class EscapeWatcher : IDisposable
{
    // Held in a field so the delegate is not collected while the hook is installed.
    private readonly Native.LowLevelKeyboardProc _proc;
    private SynchronizationContext? _sync;
    private IntPtr _hook;

    internal event Action? EscapePressed;

    internal EscapeWatcher() => _proc = HookProc;

    internal bool IsRunning => _hook != IntPtr.Zero;

    internal void Start()
    {
        if (_hook != IntPtr.Zero) return;

        // Captured here rather than in the constructor: WinForms installs its
        // synchronization context when the first Control is created, which has not
        // necessarily happened by the time this object is constructed as a field
        // initializer. Capturing too early yields null and silently drops every ESC.
        _sync = SynchronizationContext.Current;

        _hook = Native.SetWindowsHookExW(
            Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandleW(null), 0);
    }

    internal void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == Native.HC_ACTION)
        {
            var message = wParam.ToInt32();
            if (message is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN)
            {
                var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                if (info.vkCode == (uint)Keys.Escape)
                {
                    // Marshal off the hook callback; no real work happens on this thread.
                    var sync = _sync;
                    if (sync is not null) sync.Post(_ => EscapePressed?.Invoke(), null);
                    else EscapePressed?.Invoke();
                }
            }
        }

        // Always pass the key on. We observe ESC, we never swallow it.
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => Stop();
}
