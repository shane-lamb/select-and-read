namespace SelectAndRead;

/// <summary>
/// Registers global hotkeys against a message-only window and turns WM_HOTKEY into
/// events (SPEC 8.1).
/// </summary>
internal sealed class HotkeyManager : NativeWindow, IDisposable
{
    private const int CaptureId = 1;
    private const int StopId = 2;

    private readonly List<int> _registered = new();

    internal event Action? CapturePressed;
    internal event Action? StopPressed;

    /// <summary>
    /// Names of hotkeys that could not be registered because another application already
    /// owns them. Surfaced to the user as a tray balloon - silent failure here makes the
    /// app look simply broken (SPEC 2.6).
    /// </summary>
    internal List<string> Conflicts { get; } = new();

    internal HotkeyManager()
    {
        // A message-only window: never visible, exists purely to receive WM_HOTKEY.
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // HWND_MESSAGE
    }

    internal void Register(Hotkey capture, Hotkey stop)
    {
        Unregister();
        Conflicts.Clear();
        TryRegister(CaptureId, capture);
        TryRegister(StopId, stop);
    }

    private void TryRegister(int id, Hotkey hk)
    {
        if (Native.RegisterHotKey(Handle, id, hk.Modifiers | Native.MOD_NOREPEAT, hk.VirtualKey))
            _registered.Add(id);
        else
            Conflicts.Add(hk.ToString());
    }

    internal void Unregister()
    {
        foreach (var id in _registered) Native.UnregisterHotKey(Handle, id);
        _registered.Clear();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY)
        {
            switch (m.WParam.ToInt32())
            {
                case CaptureId: CapturePressed?.Invoke(); return;
                case StopId: StopPressed?.Invoke(); return;
            }
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Unregister();
        DestroyHandle();
    }
}
