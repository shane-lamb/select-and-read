using System.Drawing.Imaging;

namespace SelectAndRead;

internal static class Program
{
    private const string MutexName = @"Local\SelectAndRead.SingleInstance";
    private const string ShowSettingsEventName = @"Local\SelectAndRead.ShowSettings";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0) return RunCli(args);

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            SignalRunningInstance();
            return 0;
        }

        // Per-Monitor-V2 was already established by app.manifest before any managed code
        // ran, so ApplicationConfiguration.Initialize() is deliberately not called here -
        // it would emit a contradicting SetHighDpiMode call. See the csproj note.
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var context = new TrayAppContext();
        ListenForSecondInstance(context);
        Application.Run(context);
        return 0;
    }

    // --- Single instance --------------------------------------------------------

    /// <summary>SPEC 2.1: a second launch surfaces the running instance's settings.</summary>
    private static void SignalRunningInstance()
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(ShowSettingsEventName);
            handle.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The first instance is starting up or shutting down; nothing to signal.
        }
    }

    // Static so neither the event nor its registration is collected while the wait is
    // still outstanding; a collected handle would silently stop answering second launches.
    private static EventWaitHandle? _showSettingsEvent;
    private static RegisteredWaitHandle? _showSettingsRegistration;

    private static void ListenForSecondInstance(TrayAppContext context)
    {
        // Captured after the context has created its controls, so this is the WinForms
        // synchronization context and the callback lands on the UI thread.
        var sync = SynchronizationContext.Current;
        _showSettingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsEventName);

        _showSettingsRegistration = ThreadPool.RegisterWaitForSingleObject(
            _showSettingsEvent,
            (_, _) => sync?.Post(_ => context.ShowSettings(), null),
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: false);
    }

    // --- Debug CLI modes (SPEC 12.2) --------------------------------------------

    private static int RunCli(string[] args)
    {
        AttachToParentConsole();

        var config = Config.Load();

        try
        {
            switch (args[0])
            {
                case "--ocr-file" when args.Length >= 2:
                    return OcrFile(args[1], config);

                case "--read-file" when args.Length >= 2:
                    return ReadFile(args[1], config);

                case "--speak" when args.Length >= 2:
                    return Speak(args[1], config);

                case "--settings-metrics":
                    return SettingsMetrics(config);

                case "--capture-to" when args.Length >= 2:
                    return CaptureTo(args[1]);

                case "--freeze-to" when args.Length >= 2:
                    return FreezeTo(args[1]);

                default:
                    Console.Error.WriteLine(
                        "Usage:\n" +
                        "  SelectAndRead --ocr-file <image.png>   print recognised text\n" +
                        "  SelectAndRead --read-file <image.png>  read it via the cloud engine\n" +
                        "  SelectAndRead --speak \"<text>\"         speak text and exit\n" +
                        "  SelectAndRead --capture-to <out.png>   select a region, save it\n" +
                        "  SelectAndRead --freeze-to <out.png>    save the raw freeze frame\n" +
                        "  SelectAndRead --settings-metrics       check the dialog fits the screen");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 3;
        }
    }

    private static int OcrFile(string path, Config config)
    {
        var ocr = OcrService.Create(config.OcrLanguage);
        if (ocr is null)
        {
            Console.Error.WriteLine("No OCR language pack is installed.");
            return 3;
        }

        using var bitmap = new Bitmap(path);
        var (text, info) = ocr.RecognizeDetailedAsync(bitmap, config.UpscaleBeforeOcr)
                              .GetAwaiter().GetResult();

        Console.Error.WriteLine(
            $"[glyph height {info.MedianGlyphHeight:0.0}px, scale {info.Scale}x, {info.Words} words]");
        Console.WriteLine(text);
        return string.IsNullOrWhiteSpace(text) ? 1 : 0;
    }

    /// <summary>
    /// Runs the cloud engine against a fixture. The analogue of --ocr-file: that mode
    /// reports the glyph height and scale it chose because "the OCR was wrong" is not
    /// actionable without them, and this one reports time-to-first-audio and token usage
    /// for the same reason - "it was slow" and "it was expensive" are not actionable
    /// without measurements, and the usage figures are the only way to replace the
    /// estimated per-reading cost with a real one.
    /// </summary>
    private static int ReadFile(string path, Config config)
    {
        var key = ApiKeyStore.Load();
        if (key is null)
        {
            Console.Error.WriteLine(
                "No API key is configured. Enter one in Settings, or run the app once to create it.");
            return 3;
        }

        using var engine = new RealtimeReadingEngine(key);
        engine.ApplySettings(config);

        using var bitmap = new Bitmap(path);

        string? text;
        RealtimeReadingEngine.Diagnostics info;
        try
        {
            (text, info) = engine.ReadDetailedAsync(bitmap, CancellationToken.None)
                                 .GetAwaiter().GetResult();
        }
        catch (RealtimeException ex)
        {
            Console.Error.WriteLine($"[failed after speaking: {ex.Spoke}] {ex.Message}");
            return 3;
        }

        Console.Error.WriteLine(
            $"[first audio {info.TimeToFirstAudio.TotalMilliseconds:0}ms, " +
            $"total {info.Total.TotalMilliseconds:0}ms]");

        if (info.Usage is { } usage)
        {
            Console.Error.WriteLine(
                $"[tokens in: {usage.InputText} text, {usage.InputImage} image, " +
                $"{usage.InputCached} cached | out: {usage.OutputText} text, " +
                $"{usage.OutputAudio} audio | total {usage.Total}]");
        }
        else
        {
            Console.Error.WriteLine("[no usage reported by the server]");
        }

        Console.WriteLine(text);
        return string.IsNullOrWhiteSpace(text) ? 1 : 0;
    }

    private static int Speak(string text, Config config)
    {
        using var speech = new SpeechService();
        speech.ApplySettings(config);
        speech.SpeakAsync(text, CancellationToken.None).GetAwaiter().GetResult();
        return 0;
    }

    /// <summary>
    /// Reports whether the settings dialog actually fits on screen, and whether its Save
    /// button is reachable.
    ///
    /// The dialog grows with its content and with the system text size, so "does it still
    /// fit?" is a real question with a numeric answer, and one that is otherwise only
    /// discoverable by a human opening it on a small display. The exit code makes it usable
    /// as a check rather than just a readout.
    ///
    /// Note this is genuinely useful under `prlctl exec`, despite session 0 being unable to
    /// draw: session 0's desktop is a 1024x768 one, which is a far better proxy for a
    /// cramped real display than the VM's own 4K interactive session.
    /// </summary>
    private static int SettingsMetrics(Config config)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Rectangle working = Rectangle.Empty, form = Rectangle.Empty, save = Rectangle.Empty;
        bool scrollable = false;
        int viewport = 0, content = 0;

        using (var dialog = new SettingsForm(config))
        {
            dialog.Shown += (_, _) =>
            {
                working = Screen.FromControl(dialog).WorkingArea;
                form = dialog.Bounds;

                if (FindSaveButton(dialog) is { } button)
                    save = button.RectangleToScreen(button.ClientRectangle);

                if (FindScrollPanel(dialog) is { } panel)
                {
                    scrollable = panel.VerticalScroll.Visible;
                    viewport = panel.ClientSize.Height;
                    content = panel.VerticalScroll.Visible
                        ? panel.VerticalScroll.Maximum
                        : panel.PreferredSize.Height;
                }

                dialog.Close();
            };

            Application.Run(dialog);
        }

        Console.WriteLine($"working area : {working.Width}x{working.Height}");
        Console.WriteLine($"dialog       : {form.Width}x{form.Height} at {form.X},{form.Y}");

        if (save.IsEmpty)
        {
            Console.Error.WriteLine("Save button not found.");
            return 3;
        }

        Console.WriteLine($"save button  : {save.Width}x{save.Height} at {save.X},{save.Y}");

        var fits = working.Contains(form);
        var reachable = working.Contains(save);
        var clipped = content > viewport;

        Console.WriteLine($"content      : {content}px in a {viewport}px viewport");
        Console.WriteLine($"scrollbar    : {scrollable}");
        Console.WriteLine($"dialog fits  : {fits}");
        Console.WriteLine($"save onscreen: {reachable}");

        // Fitting entirely is nice but not required; a clamped dialog that scrolls its
        // content is a pass. The two real failures are Save being off screen, and content
        // overflowing with no scrollbar to reach it - both of which leave the dialog
        // impossible to operate.
        if (!reachable) return 1;
        return clipped && !scrollable ? 1 : 0;
    }

    private static Panel? FindScrollPanel(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (child is Panel { AutoScroll: true } panel) return panel;
            if (FindScrollPanel(child) is { } nested) return nested;
        }

        return null;
    }

    private static Button? FindSaveButton(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (child is Button { DialogResult: DialogResult.OK } button) return button;
            if (FindSaveButton(child) is { } nested) return nested;
        }

        return null;
    }

    /// <summary>
    /// Saves the raw freeze frame with no overlay involved. Separates "the capture is
    /// wrong" from "the overlay or the crop is wrong", which is otherwise guesswork - and
    /// is the fastest way to confirm the documented black-capture behaviour on protected
    /// content (SPEC 3.1).
    /// </summary>
    private static int FreezeTo(string path)
    {
        var screen = ScreenCapture.GetScreenSize();
        using var frame = ScreenCapture.CaptureScreen(screen);
        frame.Save(path, ImageFormat.Png);

        Console.WriteLine($"Saved {frame.Width}x{frame.Height} freeze frame to {path}");
        return 0;
    }

    private static int CaptureTo(string path)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var screen = ScreenCapture.GetScreenSize();
        using var frame = ScreenCapture.CaptureScreen(screen);

        using var overlay = new SelectionOverlay(frame, screen);
        overlay.ShowDialog();

        if (overlay.Selection is null)
        {
            Console.Error.WriteLine($"Cancelled: {overlay.CancelReason ?? "unknown"}");
            return 1;
        }

        using var crop = ScreenCapture.Crop(frame, overlay.Selection.Value);
        crop.Save(path, ImageFormat.Png);

        Console.WriteLine($"Saved {crop.Width}x{crop.Height} to {path}");
        return 0;
    }

    /// <summary>
    /// The app is a WinExe and has no console of its own, so borrow the launching
    /// terminal's and rebind Console.Out.
    ///
    /// The redirection check is essential rather than defensive: AttachConsole *replaces*
    /// the process's standard handles with the console's. Calling it when the launcher has
    /// already supplied a pipe or a file redirect throws that redirect away, and
    /// `--ocr-file x.png > out.txt` silently produces an empty file.
    /// </summary>
    private static void AttachToParentConsole()
    {
        var existing = Native.GetStdHandle(Native.STD_OUTPUT_HANDLE);
        var alreadyRedirected = existing != IntPtr.Zero && existing != Native.INVALID_HANDLE_VALUE;
        if (alreadyRedirected) return;

        if (!Native.AttachConsole(Native.ATTACH_PARENT_PROCESS)) return;

        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }
}
