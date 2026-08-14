using System.Drawing.Imaging;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

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

        // Only in the instance that is actually staying, and only after the mutex check,
        // so a second launch cannot rewrite the entry out from under the running one.
        Config.RepairStartWithWindowsPath();

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
                    return OcrFile(args[1], args.Contains("--spans"), config);

                case "--read-file" when args.Length >= 2:
                    return ReadFile(args[1], config);

                case "--speak" when args.Length >= 2:
                    return Speak(args[1], config);

                case "--markers":
                {
                    var probe = args.Length >= 2 && !args[1].StartsWith("--")
                        ? args[1]
                        : MarkerProbeText;
                    return Markers(probe, ValueAfter(args, "--voice"), args.Contains("--play"), config);
                }

                case "--read-local" when args.Length >= 2:
                    return ReadLocal(args[1], ValueAfter(args, "--overlay"), config);

                case "--settings-metrics":
                    return SettingsMetrics(config);

                case "--highlight-metrics":
                    return HighlightMetrics();

                case "--capture-to" when args.Length >= 2:
                    return CaptureTo(args[1]);

                case "--freeze-to" when args.Length >= 2:
                    return FreezeTo(args[1]);

                default:
                    Console.Error.WriteLine(
                        "Usage:\n" +
                        "  SelectAndRead --ocr-file <image.png> [--spans]\n" +
                        "                                        print recognised text; --spans adds word boxes\n" +
                        "  SelectAndRead --read-file <image.png>  read it via the cloud engine\n" +
                        "  SelectAndRead --speak \"<text>\"         speak text and exit\n" +
                        "  SelectAndRead --markers [\"<text>\"] [--voice <name>] [--play]\n" +
                        "                                        dump word-boundary cues per voice\n" +
                        "  SelectAndRead --capture-to <out.png>   select a region, save it\n" +
                        "  SelectAndRead --freeze-to <out.png>    save the raw freeze frame\n" +
                        "  SelectAndRead --read-local <image.png> [--overlay <x>,<y>]\n" +
                        "                                        read it locally, logging each word marked;\n" +
                        "                                        --overlay also draws the real mark on screen\n" +
                        "  SelectAndRead --settings-metrics       check the dialog fits the screen\n" +
                        "  SelectAndRead --highlight-metrics      check the word mark's shape and position");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 3;
        }
    }

    private static int OcrFile(string path, bool spans, Config config)
    {
        var ocr = OcrService.Create(config.OcrLanguage);
        if (ocr is null)
        {
            Console.Error.WriteLine("No OCR language pack is installed.");
            return 3;
        }

        using var bitmap = new Bitmap(path);
        var result = ocr.RecognizeDetailedAsync(bitmap, config.UpscaleBeforeOcr)
                        .GetAwaiter().GetResult();

        var info = result.Info;
        Console.Error.WriteLine(
            $"[glyph height {info.MedianGlyphHeight:0.0}px, scale {info.Scale}x, {info.Words} words]");

        // The span table is what the on-screen mark is driven from, and a box that is
        // plausible but wrong is invisible in the text alone - so it can be dumped in crop
        // coordinates and checked against the fixture by eye (SPEC 16.2). Opt-in, because it
        // is one line per word and would bury the fixture comparison.
        foreach (var span in spans ? result.Spans : [])
        {
            Console.Error.WriteLine(
                $"  [{span.Start,4}+{span.Length,-3}] " +
                $"{span.Box.X,5},{span.Box.Y,-5} {span.Box.Width,4}x{span.Box.Height,-4} " +
                $"\"{result.Text.Substring(span.Start, span.Length)}\"");
        }

        Console.WriteLine(result.Text);
        return string.IsNullOrWhiteSpace(result.Text) ? 1 : 0;
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

    // --- Word-boundary marker probe ---------------------------------------------

    /// <summary>
    /// Stresses the parts of tokenisation that would break an ordinal marker-to-word
    /// alignment: currency, decimals, a percentage, an abbreviation whose full stop is not a
    /// sentence end, an apostrophe, a hyphenated word and a bare year.
    /// </summary>
    private const string MarkerProbeText =
        "The quick brown fox costs $12.50, or 3.5% of Dr. Smith's e-mail budget for 2026.";

    /// <summary>How often playback position is sampled under --play.</summary>
    private const int PollIntervalMs = 50;

    /// <summary>
    /// Reports, per installed voice, whether the WinRT synthesiser emits word-boundary
    /// metadata and what it contains.
    ///
    /// This is the measurement that decides whether reading position can be shown on screen
    /// at all. The synthesiser has no SpeakProgress event, and the boundaries do *not* arrive
    /// as SpeechSynthesisStream.Markers - that list is for SSML bookmarks and stays empty
    /// here. They arrive as a timed metadata track on the MediaPlaybackItem built from the
    /// synthesised stream, whose cues are SpeechCue objects.
    ///
    /// Cue count matters less than what a cue carries. StartPositionInInput and
    /// EndPositionInInput are character offsets into the text that was handed to the
    /// synthesiser, so if they are populated, boundaries can be tied to source text exactly
    /// rather than by counting words - which the probe sentence above is built to break.
    ///
    /// Synthesis needs no audio endpoint, so this runs under `prlctl exec` in session 0.
    /// --play additionally plays the audio and tracks MediaPlaybackSession.Position against
    /// the cue table, which does need a real session (tests/vm/README.md, trap 1).
    /// </summary>
    private static int Markers(string text, string? voiceFilter, bool play, Config config)
    {
        var voices = SpeechSynthesizer.AllVoices
            .Where(v => voiceFilter is null
                || v.DisplayName.Contains(voiceFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

        Console.WriteLine($"text   : {text}");
        Console.WriteLine($"tokens : {tokens} whitespace-separated");
        Console.WriteLine($"voices : {voices.Count} installed");
        Console.WriteLine();

        var usable = 0;

        foreach (var voice in voices)
        {
            Console.WriteLine($"=== {voice.DisplayName}");
            Console.WriteLine($"    id       {voice.Id}");
            Console.WriteLine($"    language {voice.Language}");

            try
            {
                if (ProbeVoice(voice, text, tokens, play, config)) usable++;
            }
            catch (Exception ex)
            {
                // Per voice, so one that refuses to synthesise does not abort the sweep.
                Console.WriteLine($"    FAILED   {ex.GetType().Name}: {ex.Message}");
            }

            Console.WriteLine();
        }

        Console.WriteLine($"{usable} of {voices.Count} voices produced word-boundary cues.");
        return usable == 0 ? 1 : 0;
    }

    private static bool ProbeVoice(
        VoiceInformation voice, string text, int tokens, bool play, Config config)
    {
        using var synthesizer = new SpeechSynthesizer { Voice = voice };

        synthesizer.Options.IncludeWordBoundaryMetadata = true;
        synthesizer.Options.IncludeSentenceBoundaryMetadata = true;
        synthesizer.Options.SpeakingRate = Math.Clamp(config.SpeakingRate, 0.5, 6.0);

        var stream = synthesizer.SynthesizeTextToStreamAsync(text).AsTask().GetAwaiter().GetResult();

        // Reported only to keep the two mechanisms distinguishable in the output: this is the
        // SSML bookmark list, and it is empty for ordinary text however the options are set.
        Console.WriteLine($"    bookmarks {stream.Markers.Count} (SSML marks; expected 0)");

        var source = MediaSource.CreateFromStream(stream, stream.ContentType);
        var item = new MediaPlaybackItem(source);

        // The tracks are published when the source opens, not when the item is constructed,
        // so a bare read of TimedMetadataTracks here finds nothing whatever the voice did.
        source.OpenAsync().AsTask().GetAwaiter().GetResult();
        WaitForTracks(item);

        Console.WriteLine($"    tracks   {item.TimedMetadataTracks.Count}");

        var cues = new List<SpeechCue>();

        for (var index = 0; index < item.TimedMetadataTracks.Count; index++)
        {
            var track = item.TimedMetadataTracks[index];
            var speech = track.Cues.OfType<SpeechCue>().ToList();

            Console.WriteLine(
                $"      [{index}] kind {track.TimedMetadataKind}, label \"{track.Label}\", " +
                $"{track.Cues.Count} cues ({speech.Count} SpeechCue)");

            // Word and sentence boundaries arrive as separate tracks of the same kind, so the
            // label is the only thing that tells them apart.
            if (track.Label?.Contains("Word", StringComparison.OrdinalIgnoreCase) == true)
                cues.AddRange(speech);
        }

        if (cues.Count == 0)
        {
            Console.WriteLine("    verdict  NO WORD CUES");
            return false;
        }

        Console.WriteLine($"    cues     {cues.Count} word cues for {tokens} tokens");

        foreach (var (cue, index) in cues.Select((c, i) => (c, i)))
        {
            var span = cue.StartPositionInInput is { } from && cue.EndPositionInInput is { } to
                ? $"input[{from}..{to}] = {Describe(Slice(text, from, to))}"
                : "input[?..?]";

            Console.WriteLine(
                $"      #{index,-3} {cue.StartTime.TotalSeconds,7:0.000}s " +
                $"+{cue.Duration.TotalSeconds:0.000}s  {Describe(cue.Text),-26} {span}");
        }

        Console.WriteLine("    verdict  USABLE");

        if (play) TrackPlayback(item, cues);
        return true;
    }

    /// <summary>
    /// The track list is populated asynchronously even once the source has opened, so a
    /// single read races it and reports zero tracks for a voice that does emit them.
    /// </summary>
    private static void WaitForTracks(MediaPlaybackItem item)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (item.TimedMetadataTracks.Count == 0 && DateTime.UtcNow < deadline)
            Thread.Sleep(PollIntervalMs);
    }

    /// <summary>
    /// The substring a cue's input offsets point at, or a marker for offsets that do not
    /// address the text that was submitted - which is the case worth catching, since those
    /// offsets are the whole basis for tying speech position back to a recognised word.
    ///
    /// EndPositionInInput is the index of the word's last character, not one past it, so the
    /// range is closed at both ends. Measured: "The" comes back as input[0..2].
    /// </summary>
    private static string Slice(string text, int from, int to) =>
        from >= 0 && to < text.Length && to >= from ? text[from..(to + 1)] : "<out of range>";

    /// <summary>
    /// Quoted, with a length, because the outcomes that matter look alike at a glance: the
    /// word itself, an offset rendered as digits, and nothing at all.
    /// </summary>
    private static string Describe(string? value) =>
        value is null ? "<null>" : $"\"{value}\" ({value.Length} chars)";

    /// <summary>
    /// Plays the synthesised audio and samples the playback position against the cue table,
    /// which is exactly how a highlight would be driven. Three numbers come out of it.
    ///
    /// Detection lag - how far past a cue's own StartTime the poll notices it - is the one
    /// that decides whether a word-level highlight can keep up at all; a position that only
    /// updated every few hundred milliseconds would sink the idea however good the cues are.
    ///
    /// Pause drift says how much further the position runs after Pause() before settling,
    /// since MediaPlayer.Pause is asynchronous and a highlight left mid-word looks stuck.
    ///
    /// And MediaEnded's wall time is here because SpeechService.SpeakAsync settles on that
    /// event alone, so a late one is not cosmetic - it is the reading appearing to hang after
    /// the last word.
    /// </summary>
    private static void TrackPlayback(MediaPlaybackItem item, IReadOnlyList<SpeechCue> cues)
    {
        using var player = new MediaPlayer { AudioCategory = MediaPlayerAudioCategory.Speech };
        player.CommandManager.IsEnabled = false;

        // Wall clock, so "MediaEnded fired" can be compared against "the audio finished".
        // SpeechService.SpeakAsync settles on this event alone, so a late one is not a
        // cosmetic problem - it is the reading appearing to hang after the last word.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var endedAt = TimeSpan.Zero;

        var finished = new TaskCompletionSource();
        player.MediaEnded += (_, _) =>
        {
            endedAt = clock.Elapsed;
            finished.TrySetResult();
        };
        player.MediaFailed += (_, e) => finished.TrySetException(
            new InvalidOperationException(e.ErrorMessage));

        player.Source = item;

        var session = player.PlaybackSession;
        var lags = new List<double>();
        var current = -1;
        var pauseTested = false;

        player.Play();

        // Deliberately not "until MediaEnded": whether that event arrives on time is one of
        // the things being measured, so the loop ends when the audio has actually run out.
        // The cap is a backstop for a stream that never advances at all.
        var wall = System.Diagnostics.Stopwatch.StartNew();

        while (wall.Elapsed < TimeSpan.FromSeconds(90))
        {
            Thread.Sleep(PollIntervalMs);

            var position = session.Position;
            var natural = session.NaturalDuration;

            // The last cue starting at or before the current position is the word being
            // spoken - the same lookup a highlight would do.
            var index = cues.Count - 1;
            while (index >= 0 && cues[index].StartTime > position) index--;

            if (index != current)
            {
                current = index;

                if (index >= 0)
                {
                    var lag = (position - cues[index].StartTime).TotalMilliseconds;
                    lags.Add(lag);
                    Console.WriteLine(
                        $"      {position.TotalSeconds,7:0.000}s  ->  #{index,-3} " +
                        $"{Describe(cues[index].Text),-26} lag {lag,4:0}ms");
                }
            }

            if (!pauseTested && position > TimeSpan.FromSeconds(1.5))
            {
                pauseTested = true;
                player.Pause();
                var atPause = session.Position;
                Thread.Sleep(1000);
                var afterIdle = session.Position;
                Console.WriteLine(
                    $"    pause    {atPause.TotalSeconds:0.000}s -> {afterIdle.TotalSeconds:0.000}s " +
                    $"after 1000ms idle, drift {(afterIdle - atPause).TotalMilliseconds:0}ms");
                player.Play();
                continue;
            }

            if (natural > TimeSpan.Zero && position >= natural) break;
        }

        Console.WriteLine(
            $"    duration {session.NaturalDuration.TotalSeconds:0.000}s natural, " +
            $"position ran out at {wall.Elapsed.TotalSeconds:0.000}s wall " +
            $"(includes the 1.0s pause test)");

        // Given a moment, because the whole question is whether it is merely late.
        var settled = finished.Task.Wait(TimeSpan.FromSeconds(5));
        Console.WriteLine(settled
            ? $"    MediaEnded fired at {endedAt.TotalSeconds:0.000}s wall"
            : "    MediaEnded DID NOT FIRE within 5s of the audio running out");

        if (lags.Count > 0)
        {
            Console.WriteLine(
                $"    lag      {lags.Min():0}ms min, {lags.Max():0}ms max, " +
                $"{lags.Average():0}ms mean over {lags.Count} cue transitions");
        }
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
    /// Note this is genuinely useful run without `vmrun`'s `-interactive`, despite session 0
    /// being unable to draw: session 0's desktop is a 1024x768 one, which is a far better
    /// proxy for a cramped real display than the 2048x1440 working area of the VM's own
    /// interactive session. vmrun propagates the guest's exit code back to the Mac shell, so
    /// the check below is usable from there directly.
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

    /// <summary>Parses an "x,y" argument. Null for a missing or malformed one.</summary>
    private static Point? ParsePoint(string? value)
    {
        var parts = value?.Split(',');
        return parts is [var x, var y]
               && int.TryParse(x, out var px) && int.TryParse(y, out var py)
            ? new Point(px, py)
            : null;
    }

    private static string? ValueAfter(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>
    /// Runs the whole local pipeline against a fixture and logs every word the reading marks.
    ///
    /// This is the mode that would have caught the boundary metadata never being switched on:
    /// --ocr-file proves the span table, --markers proves the cues, and --highlight-metrics
    /// proves the window, yet all three pass while the one line joining them is missing and
    /// nothing is ever marked. It exercises OCR, cleaning, synthesis, the cue table, the
    /// tracking timer and the event, which is everything except the drawing.
    ///
    /// The exit code makes it a check: a reading that marks nothing is the failure.
    /// </summary>
    private static int ReadLocal(string path, string? overlayAt, Config config)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var engine = new LocalReadingEngine();
        engine.ApplySettings(config);

        var marks = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        // Standing in for the crop's origin. Point the fixture's own image at the same place
        // on screen and the marks land exactly on its words, which is the whole of what a
        // screenshot needs to show.
        var origin = ParsePoint(overlayAt);
        var overlay = origin is null ? null : new HighlightOverlay();

        engine.WordHighlighted += box =>
        {
            overlay?.Show(box is { } r && origin is { } at
                ? r with { X = r.X + at.X, Y = r.Y + at.Y }
                : null);

            if (box is not { } rect)
            {
                Console.WriteLine($"  {clock.Elapsed.TotalSeconds,7:0.000}s  cleared");
                return;
            }

            Interlocked.Increment(ref marks);
            Console.WriteLine(
                $"  {clock.Elapsed.TotalSeconds,7:0.000}s  " +
                $"{rect.X,5},{rect.Y,-5} {rect.Width,4}x{rect.Height,-4}");
        };

        using var bitmap = new Bitmap(path);

        string? text;

        if (overlay is null)
        {
            text = engine.ReadAsync(bitmap, CancellationToken.None).GetAwaiter().GetResult();
        }
        else
        {
            // The mark is a window, so it needs the message pump the interactive app gives
            // it; without one it is placed and shaped but never painted. The reading
            // therefore runs off the pump and stops it when it finishes.
            string? spoken = null;
            Exception? failure = null;

            Task.Run(async () =>
            {
                try { spoken = await engine.ReadAsync(bitmap, CancellationToken.None); }
                catch (Exception ex) { failure = ex; }
            }).ContinueWith(_ => overlay.BeginInvoke(Application.ExitThread));

            Application.Run();
            overlay.Dispose();

            if (failure is not null) throw failure;
            text = spoken;
        }

        Console.WriteLine();
        Console.WriteLine($"text  : {text}");
        Console.WriteLine($"marked: {marks} words");

        if (marks != 0) return 0;

        Console.Error.WriteLine(
            "The reading marked nothing. Either the voice published no word boundaries, or " +
            "the recognised text produced no spans - run --markers and --ocr-file --spans.");
        return 1;
    }

    /// <summary>
    /// Reports where the word mark actually put itself and what shape it actually is.
    ///
    /// The mark's whole claim is that it surrounds a word without covering it, and that claim
    /// rests on a window region - which is invisible, so a wrong one looks like a mark that
    /// is merely slightly off rather than like a bug. This asks Windows what it ended up
    /// with: the window rectangle it gave the mark, and whether the middle really is not part
    /// of the window. The exit code makes it a check rather than a readout, as with
    /// --settings-metrics.
    ///
    /// Runs under `prlctl exec` in session 0: the window is created and shaped there even
    /// though nothing can be drawn, and it is the geometry that is being asked about.
    /// </summary>
    private static int HighlightMetrics()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // An arbitrary but asymmetric word box, so a transposed coordinate cannot pass.
        var word = new Rectangle(400, 250, 120, 30);

        using var overlay = new HighlightOverlay();
        overlay.Show(word);

        // Let the shaping and positioning settle before reading them back.
        Application.DoEvents();

        Native.GetWindowRect(overlay.Handle, out var rect);
        var window = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);

        var region = Native.CreateRectRgn(0, 0, 0, 0);
        var shaped = Native.GetWindowRgn(overlay.Handle, region) != 0;

        Console.WriteLine($"word     : {word.Width}x{word.Height} at {word.X},{word.Y}");
        Console.WriteLine($"window   : {window.Width}x{window.Height} at {window.X},{window.Y}");
        Console.WriteLine($"shaped   : {shaped}");

        if (!shaped)
        {
            Native.DeleteObject(region);
            Console.Error.WriteLine("The mark has no window region, so it would cover the word.");
            return 1;
        }

        Native.GetRgnBox(region, out var box);
        Console.WriteLine(
            $"region   : {box.Right - box.Left}x{box.Bottom - box.Top} at {box.Left},{box.Top}");

        // Region coordinates are relative to the window, so the word's centre sits at the
        // centre of the window by construction.
        var centre = Native.PtInRegion(region, window.Width / 2, window.Height / 2);
        var edge = Native.PtInRegion(region, 1, window.Height / 2);
        Native.DeleteObject(region);

        Console.WriteLine($"centre in: {centre}");
        Console.WriteLine($"edge in  : {edge}");

        // The window has to sit outside the word on every side, or the mark would overlap the
        // text it is pointing at.
        var surrounds = window.Contains(word) && window != word;

        Console.WriteLine($"surrounds: {surrounds}");

        // Centred: equal clearance on both axes, which is what makes the mark read as a box
        // around the word rather than as an offset smear.
        var centred = word.X - window.X == window.Right - word.Right
                      && word.Y - window.Y == window.Bottom - word.Bottom;

        Console.WriteLine($"centred  : {centred}");

        // The two failures that matter: a mark that covers its word, and one in the wrong
        // place. A solid centre means the region never got cut.
        if (centre) return 1;
        return surrounds && centred && edge ? 0 : 1;
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
