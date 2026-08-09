using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SelectAndRead;

/// <summary>
/// Tray icon, menu and the Idle/Selecting/Working/Speaking/Paused state machine (SPEC 2.1).
/// </summary>
internal sealed class TrayAppContext : ApplicationContext
{
    private enum State { Idle, Selecting, Working, Speaking, Paused }

    private readonly NotifyIcon _tray;
    private readonly Icon _icon;
    private readonly HotkeyManager _hotkeys = new();
    private readonly LocalReadingEngine _local = new();
    private readonly EscapeWatcher _escape = new();
    private readonly ToolStripMenuItem _stopItem;
    private readonly ToolStripMenuItem _replayItem;

    private Config _config;

    /// <summary>Non-null only while the cloud engine is enabled and a key is configured.</summary>
    private RealtimeReadingEngine? _cloud;

    private State _state = State.Idle;

    /// <summary>
    /// Whichever engine produced the current or most recent audio. The playback hotkey needs
    /// this because either engine could have spoken last - a cloud reading that failed before
    /// saying anything hands over to the local one mid-capture (SPEC 14.4).
    /// </summary>
    private IReadingEngine? _lastSpoken;

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

        _replayItem = new ToolStripMenuItem("Replay last reading", null, (_, _) => BeginReplay())
        {
            Enabled = false,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Read a selection", null, (_, _) => BeginCapture()));
        menu.Items.Add(_stopItem);
        menu.Items.Add(_replayItem);
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
        // Nothing is bound to the double click: it raises Click first, so capture on the
        // double click would open Settings and then throw the overlay over the top of it.
        // Capture keeps the hotkey and the menu item.
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowSettings();
        };

        _hotkeys.CapturePressed += OnCaptureHotkey;
        _hotkeys.PlaybackPressed += OnPlaybackHotkey;
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

        // The engine about to be discarded takes its retained audio with it, so nothing may
        // still be pointing at it: replaying through a disposed engine throws from the
        // CancellationTokenSource before it reaches the audio.
        if (_cloud is not null && ReferenceEquals(_lastSpoken, _cloud))
        {
            _lastSpoken = null;
            SetReplayEnabled(false);
        }

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
        _hotkeys.Register(_config.Capture, _config.Playback);

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
        if (_state is State.Speaking or State.Paused) StopSpeaking();
        BeginCapture();
    }

    /// <summary>
    /// One hotkey, meaning whichever of pause, resume and replay fits the current state
    /// (SPEC 2.5). Abandoning a reading outright is ESC's job, not this one's.
    ///
    /// The <see cref="IReadingEngine.IsSpeaking"/> test is what keeps this honest. Speaking
    /// is entered before recognition or the cloud request even begins, so state alone cannot
    /// tell a reading that is talking from one that has not started - and pausing something
    /// inaudible would look exactly like the app having hung.
    /// </summary>
    private void OnPlaybackHotkey()
    {
        switch (_state)
        {
            case State.Paused:
                _lastSpoken?.Resume();
                _state = State.Speaking;
                break;

            case State.Speaking when _lastSpoken is { IsSpeaking: true } engine:
                engine.Pause();
                _state = State.Paused;
                break;

            case State.Idle when _lastSpoken is { CanReplay: true }:
                BeginReplay();
                break;

            // Selecting, Working, a reading still being prepared, and an Idle app with
            // nothing to replay: there is nothing sensible to do, so do nothing.
        }
    }

    // --- Capture pipeline -------------------------------------------------------

    private async void BeginCapture()
    {
        if (_state is State.Selecting or State.Working) return;
        if (_state is State.Speaking or State.Paused) StopSpeaking();

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
                _local,
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
                    _cloud, () => _cloud.ReadAsync(crop, CancellationToken.None), operationId);
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
            _local, () => _local.ReadAsync(crop, CancellationToken.None), operationId);
    }

    /// <summary>
    /// Plays the last reading again from the beginning (SPEC 2.5). Nothing is returned for
    /// the clipboard: the text went there when the reading was first produced, and copying
    /// it a second time would be busywork at best and would clobber whatever the user has
    /// copied since at worst.
    /// </summary>
    private async void BeginReplay()
    {
        // Self-guarding rather than relying on the menu item's enablement, which is only
        // refreshed between readings.
        if (_state != State.Idle) return;
        if (_lastSpoken is not { CanReplay: true } engine) return;

        var operationId = ++_operationId;

        try
        {
            await InSpeakingStateAsync(
                engine,
                async () =>
                {
                    await engine.ReplayAsync(CancellationToken.None);
                    return (string?)null;
                },
                operationId);
        }
        catch (Exception ex)
        {
            // Caught unconditionally, reported conditionally. InSpeakingStateAsync rethrows
            // RealtimeException so ReadAsync can decide about falling back; there is no
            // fallback for a replay, and an escaping exception from an async void method
            // takes the process down - so a superseded replay must still be swallowed here,
            // just not announced. State is already back to Idle: the same operationId guard
            // governs InSpeakingStateAsync's finally.
            if (operationId == _operationId) Report("Replay failed", ex.Message);
        }
    }

    /// <summary>
    /// Runs one reading inside the Speaking state, owning the ESC hook lifecycle
    /// (SPEC 8.2), the menu enablement and the clipboard copy - all of which are
    /// identical whichever engine produced the audio, and whether it is a first reading or a
    /// replay.
    /// </summary>
    private async Task InSpeakingStateAsync(
        IReadingEngine engine, Func<Task<string?>> read, int operationId)
    {
        _lastSpoken = engine;
        _state = State.Speaking;
        SetStopEnabled(true);
        SetReplayEnabled(false);
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
                SetReplayEnabled(engine.CanReplay);
            }
        }
    }

    private void Report(string title, string message, ToolTipIcon icon = ToolTipIcon.Error) =>
        _tray.ShowBalloonTip(6000, title, message, icon);

    /// <summary>
    /// A hard stop: playback ends and its position is forgotten. Deliberately not the same as
    /// the playback hotkey's pause - ESC has to keep meaning "get me out of this" whatever
    /// the app is doing (SPEC 1.1, principle 3), and a toggle cannot mean that.
    ///
    /// The reading stays replayable, so the playback hotkey means the same thing afterwards
    /// as it does after a reading that finished on its own.
    /// </summary>
    private void StopSpeaking()
    {
        // Both engines, not just the active one: a cloud reading that failed over mid-run
        // can leave the local engine speaking while _cloud is still the selected engine.
        _local.Stop();
        _cloud?.Stop();
        _escape.Stop();
        if (_state is State.Speaking or State.Paused) _state = State.Idle;
        SetStopEnabled(false);
    }

    private void SetStopEnabled(bool enabled) => SetMenuEnabled(_stopItem, enabled);

    private void SetReplayEnabled(bool enabled) => SetMenuEnabled(_replayItem, enabled);

    private static void SetMenuEnabled(ToolStripMenuItem item, bool enabled)
    {
        if (item.GetCurrentParent() is { InvokeRequired: true } parent)
            parent.BeginInvoke(() => item.Enabled = enabled);
        else
            item.Enabled = enabled;
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

    /// <summary>
    /// The dialog while it is open, so a second request surfaces it instead of stacking
    /// another copy on top.
    /// </summary>
    private SettingsForm? _settings;

    internal void ShowSettings()
    {
        if (_settings is not null)
        {
            // Best effort, as in SelectionOverlay: the foreground lock refuses this when the
            // request came from a second launch, which leaves the taskbar button flashing.
            Native.SetForegroundWindow(_settings.Handle);
            _settings.Activate();
            return;
        }

        using var form = new SettingsForm(_config);
        _settings = form;

        DialogResult result;
        try
        {
            result = form.ShowDialog();
        }
        finally
        {
            _settings = null;
        }

        if (result != DialogResult.OK) return;

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

    /// <summary>
    /// The three sizes for the icon are fractions of the canvas rather than pixel constants, because
    /// <c>icon.ico</c> is drawn from the same fractions and the two must stay the same
    /// mark.
    /// </summary>
    private const float RingFraction = 0.04f;
    private const float LetterFraction = 0.70f;

    private static Icon CreateTrayIcon()
    {
        const int size = 32;
        const float ring = size * RingFraction;

        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);

        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var dark = new SolidBrush(Color.Black);
            using var light = new SolidBrush(Color.White);
            g.FillEllipse(light, 0, 0, size - 1, size - 1);
            g.FillEllipse(dark, ring, ring, size - 1 - ring * 2, size - 1 - ring * 2);

            // Centred on the glyph's ink, not on a centred StringFormat. The latter centres
            // the font's line box, whose ascent and descent are not symmetric about the
            // cap-to-baseline block a lone "S" actually occupies, so it lands the letter
            // roughly 4% of the icon low — visible in a 16px tray icon, and dependent on
            // whichever font the system hands back.
            using var glyph = new GraphicsPath();
            glyph.AddString(
                "S",
                SystemFonts.DefaultFont.FontFamily,
                (int)FontStyle.Bold,
                size * LetterFraction,
                PointF.Empty,
                StringFormat.GenericTypographic);

            var ink = glyph.GetBounds();
            using (var centre = new Matrix())
            {
                centre.Translate((size - ink.Width) / 2 - ink.X, (size - ink.Height) / 2 - ink.Y);
                glyph.Transform(centre);
            }

            g.FillPath(light, glyph);
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
