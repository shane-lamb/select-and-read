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
    bool IsSpeaking { get; }

    void ApplySettings(Config config);

    /// <summary>
    /// Reads the crop aloud, returning the recognised text for the clipboard, or null when
    /// there was nothing to copy.
    ///
    /// A null return does not mean nothing was spoken: status messages ("No text found.")
    /// are spoken by the engine but deliberately never reach the clipboard, which is the
    /// behaviour the local pipeline has always had.
    /// </summary>
    Task<string?> ReadAsync(Bitmap crop, CancellationToken cancellationToken);

    /// <summary>Immediate, and safe to call in any state including when idle (SPEC 7.5).</summary>
    void Stop();
}

/// <summary>
/// The original pipeline: Windows OCR, text cleanup, then Windows speech synthesis
/// (SPEC 5-7). Behaviour is unchanged from when this lived inline in TrayAppContext -
/// including that the OCR engine is built lazily and cached, since construction is not free.
/// </summary>
internal sealed class LocalReadingEngine : IReadingEngine
{
    private readonly SpeechService _speech = new();

    private OcrService? _ocr;
    private string? _ocrLanguage;
    private bool _upscale = true;

    public bool IsSpeaking => _speech.IsSpeaking;

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
        await _speech.SpeakAsync(message, cancellationToken);
        return null;
    }

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
