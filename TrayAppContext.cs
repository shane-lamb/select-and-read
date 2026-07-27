using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SelectAndRead;

/// <summary>Tray icon, menu and the Idle/Selecting/Working/Speaking state machine (SPEC 2.1).</summary>
internal sealed class TrayAppContext : ApplicationContext
{
    private enum State { Idle, Selecting, Working, Speaking }

    private readonly NotifyIcon _tray;
    private readonly Icon _icon;
    private readonly HotkeyManager _hotkeys = new();
    private readonly SpeechService _speech = new();
    private readonly EscapeWatcher _escape = new();
    private readonly ToolStripMenuItem _stopItem;

    private Config _config;
    private OcrService? _ocr;
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

        _speech.ApplySettings(_config);
        RegisterHotkeys();
        UpdateTooltip();
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

    private async Task RunPipelineAsync(int operationId)
    {
        var utterance = await RecognizeSelectionAsync();
        if (utterance is null) return;              // cancelled

        await SpeakAsync(utterance, operationId);
    }

    /// <summary>
    /// Select, capture and recognise. Returns the text to speak, or null if the user
    /// cancelled.
    ///
    /// Kept separate from speaking so the freeze frame - tens of megabytes at 4K - is
    /// released before playback starts, rather than being held alive for the whole
    /// reading.
    /// </summary>
    private async Task<string?> RecognizeSelectionAsync()
    {
        _state = State.Selecting;

        var screen = ScreenCapture.GetScreenSize();

        // SPEC 2.2: freeze frame first, so the overlay can never contaminate the capture.
        using var frame = ScreenCapture.CaptureScreen(screen);

        Rectangle? selection;
        using (var overlay = new SelectionOverlay(frame, screen))
        {
            overlay.ShowDialog();
            selection = overlay.Selection;
        }

        if (selection is null)
        {
            _state = State.Idle;
            return null;
        }

        _state = State.Working;

        using var crop = ScreenCapture.Crop(frame, selection.Value);

        // Almost always protected/DRM content, which captures as solid black.
        if (ScreenCapture.LooksBlank(crop)) return "Capture failed.";

        _ocr ??= OcrService.Create(_config.OcrLanguage);
        if (_ocr is null)
        {
            return "No text recognition language is installed. " +
                   "Add one in Settings, under Time and language.";
        }

        var text = await _ocr.RecognizeAsync(crop, _config.UpscaleBeforeOcr);

        if (string.IsNullOrWhiteSpace(text)) return "No text found.";

        if (_config.CopyToClipboard) TrySetClipboard(text);

        return text;
    }

    // --- Speech -----------------------------------------------------------------

    private async Task SpeakAsync(string text, int operationId)
    {
        _state = State.Speaking;
        SetStopEnabled(true);
        _escape.Start();

        try
        {
            await _speech.SpeakAsync(text, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Expected whenever the user stops playback.
        }
        catch (Exception ex) when (operationId == _operationId)
        {
            _tray.ShowBalloonTip(6000, "Speech failed", ex.Message, ToolTipIcon.Error);
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

    private void StopSpeaking()
    {
        _speech.Stop();
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

        var previousLanguage = _config.OcrLanguage;
        _config = form.Result;
        _config.Save();

        Config.ApplyStartWithWindows(_config.StartWithWindows);
        _speech.ApplySettings(_config);
        RegisterHotkeys();
        UpdateTooltip();

        // Only rebuild the engine if the language actually changed; construction is not free.
        if (previousLanguage != _config.OcrLanguage) _ocr = null;
    }

    // --- Tray icon --------------------------------------------------------------

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
            _speech.Dispose();
        }
        base.Dispose(disposing);
    }
}
