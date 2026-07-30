using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SelectAndRead;

/// <summary>Tray icon, menu and the Idle/Selecting/Working/Speaking state machine (SPEC 2.1).</summary>
internal sealed class TrayAppContext : ApplicationContext
{
    private enum State { Idle, Selecting, Working, Speaking }

    private readonly NotifyIcon _tray;
    private readonly Icon _icon;
    private readonly HotkeyManager _hotkeys = new();
    private readonly LocalReadingEngine _local = new();
    private readonly EscapeWatcher _escape = new();
    private readonly ToolStripMenuItem _stopItem;

    private Config _config;

    /// <summary>Non-null only while the cloud engine is enabled and a key is configured.</summary>
    private RealtimeReadingEngine? _cloud;

    private State _state = State.Idle;

    /// <summary>
    /// Incremented for each capture. Because stopping a reading resolves the previous
    /// run's await asynchronously, that run's continuation can execute *after* a new
    /// capture has already started; comparing against this id lets a superseded run
    /// finish quietly instead of clobbering the current one's state.
    /// </summary>
    private int _operationId;

    internal TrayAppContext()
    {
        _config = Config.Load();
        _icon = CreateTrayIcon();

        _stopItem = new ToolStripMenuItem("Stop reading", null, (_, _) => StopSpeaking())
        {
            Enabled = false,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Read a selection", null, (_, _) => BeginCapture()));
        menu.Items.Add(_stopItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Settings...", null, (_, _) => ShowSettings()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem($"Select and Read v{AppVersion}") { Enabled = false });
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApp()));

        _tray = new NotifyIcon
        {
            Icon = _icon,
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => BeginCapture();

        _hotkeys.CapturePressed += OnCaptureHotkey;
        _hotkeys.StopPressed += StopSpeaking;
        _escape.EscapePressed += StopSpeaking;

        ApplyEngineSettings();
        RegisterHotkeys();
        UpdateTooltip();
    }

    // --- Engine selection (SPEC 14.1) -------------------------------------------

    /// <summary>
    /// Rebuilds the cloud engine to match the current config, and pushes settings into
    /// both engines. The local engine always exists: it is the default, and it is the
    /// fallback when a cloud reading fails.
    /// </summary>
    private void ApplyEngineSettings()
    {
        _local.ApplySettings(_config);

        var key = _config.UseCloudEngine ? ApiKeyStore.Load() : null;

        // Rebuilt unconditionally rather than reused: the key is immutable in the engine,
        // so a reused instance would keep authenticating with the old one after the user
        // pastes a new key. Construction is trivial here, unlike OcrService.
        _cloud?.Dispose();
        _cloud = key is null ? null : new RealtimeReadingEngine(key);
        _cloud?.ApplySettings(_config);
    }


    // --- Hotkeys ----------------------------------------------------------------

    private void RegisterHotkeys()
    {
        _hotkeys.Register(_config.Capture, _config.Stop);

        // SPEC 2.6: a silently stolen hotkey makes the app look simply broken.
        if (_hotkeys.Conflicts.Count > 0)
        {
            _tray.ShowBalloonTip(
                8000,
                "Hotkey unavailable",
                $"Another application already owns {string.Join(" and ", _hotkeys.Conflicts)}. " +
                "Pick a different one in Settings.",
                ToolTipIcon.Warning);
        }
    }

    private void UpdateTooltip()
    {
        // NotifyIcon.Text is capped at 63 characters.
        var text = $"Select and Read — {_config.Capture} to read";
        _tray.Text = text.Length > 63 ? text[..63] : text;
    }

    private void OnCaptureHotkey()
    {
        // SPEC 2.5: pressing capture while reading stops it and starts a fresh selection.
        if (_state == State.Speaking) StopSpeaking();
        BeginCapture();
    }

    // --- Capture pipeline -------------------------------------------------------

    private async void BeginCapture()
    {
        if (_state is State.Selecting or State.Working) return;
        if (_state == State.Speaking) StopSpeaking();

        // Stamps this run so a superseded one cannot write state belonging to its
        // replacement. See the _operationId note on the field.
        var operationId = ++_operationId;

        try
        {
            await RunPipelineAsync(operationId);
        }
        catch (Exception ex) when (operationId == _operationId)
        {
            _state = State.Idle;
            _tray.ShowBalloonTip(6000, "Select and Read failed", ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            if (operationId == _operationId && _state is State.Selecting or State.Working)
            {
                _state = State.Idle;
                SetStopEnabled(false);
            }
        }
    }

    /// <summary>
    /// Select, capture, then read aloud.
    ///
    /// The freeze frame - tens of megabytes at 4K - is scoped to the block below and
    /// released as soon as the crop exists, rather than being held alive for the whole
    /// reading. That scoping is load-bearing, not tidiness: it is the reason the original
    /// code split selection from playback across two methods, and a plain method-scoped
    /// `using` here would silently pin a full screenshot in memory for the duration of
    /// every reading.
    /// </summary>
    private async Task RunPipelineAsync(int operationId)
    {
        _state = State.Selecting;

        var screen = ScreenCapture.GetScreenSize();

        Bitmap crop;
        using (var frame = ScreenCapture.CaptureScreen(screen))
        {
            // SPEC 2.2: freeze frame first, so the overlay can never contaminate the capture.
            Rectangle? selection;
            using (var overlay = new SelectionOverlay(frame, screen))
            {
                overlay.ShowDialog();
                selection = overlay.Selection;
            }

            if (selection is null)
            {
                _state = State.Idle;
                return;                             // cancelled
            }

            _state = State.Working;
            crop = ScreenCapture.Crop(frame, selection.Value);
        }

        using var _ = crop;

        // Almost always protected/DRM content, which captures as solid black. Caught here
        // rather than in an engine: it is a property of the capture, and sending a black
        // rectangle to a paid API would be a waste.
        if (ScreenCapture.LooksBlank(crop))
        {
            await InSpeakingStateAsync(
                () => _local.SpeakStatusAsync("Capture failed.", CancellationToken.None),
                operationId);
            return;
        }

        await ReadAsync(crop, operationId);
    }

    // --- Reading ----------------------------------------------------------------

    /// <summary>
    /// Runs the active engine, falling back to the local pipeline when a cloud reading
    /// fails before it managed to say anything. Once the cloud engine has started speaking
    /// a failure is reported rather than retried - restarting the page from the top would
    /// be more disruptive than the truncation.
    /// </summary>
    private async Task ReadAsync(Bitmap crop, int operationId)
    {
        if (_cloud is not null)
        {
            try
            {
                await InSpeakingStateAsync(
                    () => _cloud.ReadAsync(crop, CancellationToken.None), operationId);
                return;
            }
            catch (RealtimeException ex)
            {
                // A capture that has already been superseded must neither report nor fall
                // back: the balloon would be stale, and the local reading would talk over
                // the selection the user has since started.
                if (operationId != _operationId) return;

                if (ex.Spoke)
                {
                    Report("Reading interrupted", ex.Message);
                    return;
                }

                Report("Using local reading", ex.Message, ToolTipIcon.Warning);
            }
        }

        await InSpeakingStateAsync(
            () => _local.ReadAsync(crop, CancellationToken.None), operationId);
    }

    /// <summary>
    /// Runs one reading inside the Speaking state, owning the ESC hook lifecycle
    /// (SPEC 8.3), the stop-menu enablement and the clipboard copy - all of which are
    /// identical whichever engine produced the audio.
    /// </summary>
    private async Task InSpeakingStateAsync(Func<Task<string?>> read, int operationId)
    {
        _state = State.Speaking;
        SetStopEnabled(true);
        _escape.Start();

        try
        {
            var text = await read();

            // A null return means the engine spoke a status message rather than page
            // content, and status messages must never reach the clipboard.
            if (text is not null && _config.CopyToClipboard) TrySetClipboard(text);
        }
        catch (OperationCanceledException)
        {
            // Expected whenever the user stops playback.
        }
        catch (RealtimeException)
        {
            throw;                                  // ReadAsync decides whether to fall back
        }
        catch (Exception ex) when (operationId == _operationId)
        {
            Report("Speech failed", ex.Message);
        }
        finally
        {
            // Without this guard, a reading cancelled by a fresh capture would reset the
            // state its replacement had already moved on to, letting a second overlay open
            // on top of the first.
            if (operationId == _operationId)
            {
                _escape.Stop();
                _state = State.Idle;
                SetStopEnabled(false);
            }
        }
    }

    private void Report(string title, string message, ToolTipIcon icon = ToolTipIcon.Error) =>
        _tray.ShowBalloonTip(6000, title, message, icon);

    private void StopSpeaking()
    {
        // Both engines, not just the active one: a cloud reading that failed over mid-run
        // can leave the local engine speaking while _cloud is still the selected engine.
        _local.Stop();
        _cloud?.Stop();
        _escape.Stop();
        if (_state == State.Speaking) _state = State.Idle;
        SetStopEnabled(false);
    }

    private void SetStopEnabled(bool enabled)
    {
        if (_stopItem.GetCurrentParent() is { InvokeRequired: true } parent)
            parent.BeginInvoke(() => _stopItem.Enabled = enabled);
        else
            _stopItem.Enabled = enabled;
    }

    // --- Clipboard (SPEC 9) -----------------------------------------------------

    /// <summary>
    /// The Windows clipboard genuinely fails intermittently when another process holds it
    /// open, so one retry is worthwhile. A clipboard failure must never abort the speech,
    /// hence the swallow.
    /// </summary>
    private static void TrySetClipboard(string text)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch (ExternalException)
            {
                Thread.Sleep(80);
            }
        }
    }

    // --- Settings ---------------------------------------------------------------

    internal void ShowSettings()
    {
        using var form = new SettingsForm(_config);
        if (form.ShowDialog() != DialogResult.OK) return;

        _config = form.Result;
        _config.Save();
        ApiKeyStore.Save(form.ApiKey);

        Config.ApplyStartWithWindows(_config.StartWithWindows);

        // Rebuilds the cloud engine and pushes the new settings into both. The local
        // engine discards its cached OcrService here only if the language changed.
        ApplyEngineSettings();

        RegisterHotkeys();
        UpdateTooltip();
    }

    // --- Tray icon --------------------------------------------------------------

    /// <summary>
    /// The &lt;Version&gt; from the csproj, shown in the tray menu so a bug report can name
    /// the build it came from. Read from the assembly rather than hardcoded, which keeps
    /// the csproj the single source of truth (SPEC 12.3). Uses the attribute and not
    /// <c>Assembly.Location</c>, which is empty under <c>PublishSingleFile</c>.
    /// </summary>
    private static string AppVersion =>
        typeof(TrayAppContext).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    private static Icon CreateTrayIcon()
    {
        using var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb);

        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var fill = new SolidBrush(Color.FromArgb(90, 160, 255));
            g.FillEllipse(fill, 1, 1, 30, 30);

            using var font = new Font(SystemFonts.DefaultFont.FontFamily, 18, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fore = new SolidBrush(Color.White);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString("A", font, fore, new RectangleF(0, 0, 32, 32), format);
        }

        // Icon.FromHandle does not take ownership, so clone into a managed icon and
        // release the HICON rather than leaking it.
        var handle = bmp.GetHicon();
        try
        {
            using var unowned = Icon.FromHandle(handle);
            return (Icon)unowned.Clone();
        }
        finally
        {
            Native.DestroyIcon(handle);
        }
    }

    // --- Shutdown ---------------------------------------------------------------

    private void ExitApp()
    {
        _tray.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _icon.Dispose();
            _hotkeys.Dispose();
            _escape.Dispose();
            _local.Dispose();
            _cloud?.Dispose();
        }
        base.Dispose(disposing);
    }
}
