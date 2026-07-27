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

                case "--speak" when args.Length >= 2:
                    return Speak(args[1], config);

                case "--capture-to" when args.Length >= 2:
                    return CaptureTo(args[1]);

                case "--freeze-to" when args.Length >= 2:
                    return FreezeTo(args[1]);

                default:
                    Console.Error.WriteLine(
                        "Usage:\n" +
                        "  SelectAndRead --ocr-file <image.png>   print recognised text\n" +
                        "  SelectAndRead --speak \"<text>\"         speak text and exit\n" +
                        "  SelectAndRead --capture-to <out.png>   select a region, save it");
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

    private static int Speak(string text, Config config)
    {
        using var speech = new SpeechService();
        speech.ApplySettings(config);
        speech.SpeakAsync(text, CancellationToken.None).GetAwaiter().GetResult();
        return 0;
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
