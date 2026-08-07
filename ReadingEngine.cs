using System.Drawing.Imaging;

namespace SelectAndRead;

/// <summary>
/// Turns a crop into speech. The seam between the local pipeline (Windows OCR then SAPI)
/// and the cloud one (one Realtime call that returns audio directly).
///
/// This is wider than <see cref="ISpeechEngine"/> on purpose. That interface takes text,
/// which presumes recognition has already finished - true locally, but the whole point of
/// the cloud path is that audio starts arriving before any text exists. Anything narrower
/// than "crop in, speech out" cannot express it.
/// </summary>
internal interface IReadingEngine : IDisposable
{
    /// <summary>
    /// True from the moment audio starts until the reading ends, is stopped or fails -
    /// including while it is paused. It distinguishes a reading that can be paused from one
    /// still in recognition or still connecting, which is the whole basis of the playback
    /// hotkey's dispatch (SPEC 2.5).
    /// </summary>
    bool IsSpeaking { get; }

    void ApplySettings(Config config);

    /// <summary>
    /// Reads the crop aloud, returning the recognised text for the clipboard, or null when
    /// there was nothing to copy.
    ///
    /// A null return does not mean nothing was spoken: status messages ("No text found.")
    /// are spoken by the engine but deliberately never reach the clipboard.
    /// </summary>
    Task<string?> ReadAsync(Bitmap crop, CancellationToken cancellationToken);

    /// <summary>Holds the reading where it is, leaving it resumable (SPEC 2.5).</summary>
    void Pause();

    /// <summary>Continues from where <see cref="Pause"/> left off.</summary>
    void Resume();

    /// <summary>Whether the last reading can still be played again from the beginning.</summary>
    bool CanReplay { get; }

    /// <summary>
    /// Reads the last reading out again from the beginning. Nothing is returned for the
    /// clipboard: the text went there when the reading was first produced, and a replay is
    /// not new content.
    /// </summary>
    Task ReplayAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Drops whatever <see cref="ReplayAsync"/> would have played. Deliberately not part of
    /// <see cref="Stop"/>: a stopped reading stays replayable, so the playback hotkey means
    /// the same thing however the last reading ended (SPEC 2.5).
    /// </summary>
    void DiscardReplay();

    /// <summary>Immediate, and safe to call in any state including when idle (SPEC 7.5).</summary>
    void Stop();
}

/// <summary>
/// The local pipeline: Windows OCR, text cleanup, then Windows speech synthesis (SPEC 5-7).
/// The OCR engine is built lazily and cached, since construction is not free.
/// </summary>
internal sealed class LocalReadingEngine : IReadingEngine
{
    private readonly SpeechService _speech = new();

    private OcrService? _ocr;
    private string? _ocrLanguage;
    private bool _upscale = true;

    /// <summary>
    /// What a replay says. Text, not audio: synthesis is local, free and quick, so keeping
    /// megabytes of PCM alive to save a few hundred milliseconds would be the wrong trade.
    /// The cloud engine cannot make that choice - see RealtimeReadingEngine.
    /// </summary>
    private string? _lastSpoken;

    public bool IsSpeaking => _speech.IsSpeaking;

    public bool CanReplay => _lastSpoken is not null;

    public void DiscardReplay() => _lastSpoken = null;

    public void ApplySettings(Config config)
    {
        _speech.ApplySettings(config);
        _upscale = config.UpscaleBeforeOcr;

        // Only discard the engine when the language actually changed.
        if (_ocrLanguage != config.OcrLanguage)
        {
            _ocrLanguage = config.OcrLanguage;
            _ocr = null;
        }
    }

    public async Task<string?> ReadAsync(Bitmap crop, CancellationToken cancellationToken)
    {
        // A new reading is the one thing that supersedes the last one; every other way a
        // reading can end leaves it replayable.
        _lastSpoken = null;

        _ocr ??= OcrService.Create(_ocrLanguage);

        if (_ocr is null)
        {
            await SpeakStatusAsync(
                "No text recognition language is installed. " +
                "Add one in Settings, under Time and language.",
                cancellationToken);
            return null;
        }

        var text = await _ocr.RecognizeAsync(crop, _upscale);

        if (string.IsNullOrWhiteSpace(text))
        {
            await SpeakStatusAsync("No text found.", cancellationToken);
            return null;
        }

        _lastSpoken = text;
        await _speech.SpeakAsync(text, cancellationToken);
        return text;
    }

    /// <summary>
    /// Speaks a status message. Returns null so callers can copy the result to the
    /// clipboard unconditionally: a status message is not page content and must never
    /// land there.
    /// </summary>
    internal async Task<string?> SpeakStatusAsync(string message, CancellationToken cancellationToken)
    {
        // Retained like any other reading: "did it say no text found, or capture failed?" is
        // exactly the question a user who missed it wants the replay hotkey to answer.
        _lastSpoken = message;
        await _speech.SpeakAsync(message, cancellationToken);
        return null;
    }

    public async Task ReplayAsync(CancellationToken cancellationToken)
    {
        if (_lastSpoken is not { } text) return;
        await _speech.SpeakAsync(text, cancellationToken);
    }

    public void Pause() => _speech.Pause();

    public void Resume() => _speech.Resume();

    public void Stop() => _speech.Stop();

    public void Dispose() => _speech.Dispose();
}

internal static class BitmapExtensions
{
    /// <summary>
    /// Encodes the crop for upload. PNG rather than JPEG deliberately: screen text is
    /// exactly the high-contrast, hard-edged content that JPEG artefacts damage most, and
    /// the crops are small enough that the size difference does not matter.
    /// </summary>
    internal static byte[] ToPng(this Bitmap bitmap)
    {
        using var memory = new MemoryStream();
        bitmap.Save(memory, ImageFormat.Png);
        return memory.ToArray();
    }
}
