using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace SelectAndRead;

/// <summary>Windows' built-in OCR engine, with the pre-processing that makes it accurate
/// on small UI text (SPEC 5).</summary>
internal sealed class OcrService
{
    /// <summary>The engine rejects images smaller than this on either axis.</summary>
    private const int MinEngineDimension = 40;

    /// <summary>Upscale small crops until the shorter side is about this many pixels.</summary>
    private const int TargetShortSide = 1000;

    private const int MaxUpscaleFactor = 4;

    private readonly OcrEngine _engine;

    private OcrService(OcrEngine engine) => _engine = engine;

    internal string RecognizerLanguage => _engine.RecognizerLanguage.LanguageTag;

    /// <summary>
    /// Resolves an engine in the order given by SPEC 5.1. Returns null when the machine
    /// has no usable OCR language pack at all.
    /// </summary>
    internal static OcrService? Create(string? language)
    {
        OcrEngine? engine = null;

        if (!string.IsNullOrWhiteSpace(language))
        {
            try { engine = OcrEngine.TryCreateFromLanguage(new Language(language)); }
            catch (ArgumentException) { /* malformed tag in config; fall through */ }
        }

        engine ??= OcrEngine.TryCreateFromUserProfileLanguages();

        if (engine is null)
        {
            try { engine = OcrEngine.TryCreateFromLanguage(new Language("en-US")); }
            catch (ArgumentException) { /* give up below */ }
        }

        return engine is null ? null : new OcrService(engine);
    }

    internal static IReadOnlyList<string> AvailableLanguages() =>
        OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();

    /// <summary>Recognises the crop and returns speech-ready text (may be empty).</summary>
    internal async Task<string> RecognizeAsync(Bitmap crop, bool upscale)
    {
        var factor = upscale ? ChooseScale(crop.Width, crop.Height) : 1;

        Bitmap prepared = factor > 1 ? Upscale(crop, factor) : crop;
        try
        {
            var softwareBitmap = await ToSoftwareBitmapAsync(prepared);
            using (softwareBitmap)
            {
                var result = await _engine.RecognizeAsync(softwareBitmap);
                return TextCleaner.Clean(result.Lines.Select(l => l.Text));
            }
        }
        finally
        {
            if (!ReferenceEquals(prepared, crop)) prepared.Dispose();
        }
    }

    /// <summary>
    /// SPEC 5.2. Windows OCR is tuned for document-scale text and is markedly weaker on
    /// the 10-12px UI text this tool captures most often, so upscaling first is the
    /// cheapest accuracy win available.
    /// </summary>
    internal static int ChooseScale(int width, int height)
    {
        var max = (int)Math.Min(int.MaxValue, OcrEngine.MaxImageDimension);
        var shorter = Math.Max(1, Math.Min(width, height));

        var factor = shorter >= TargetShortSide
            ? 1
            : Math.Clamp((int)Math.Ceiling((double)TargetShortSide / shorter), 1, MaxUpscaleFactor);

        // Never exceed what the engine will accept.
        while (factor > 1 && ((long)width * factor > max || (long)height * factor > max))
            factor--;

        // Conversely, a very small crop must be grown past the engine's floor or it is
        // rejected outright - this can legitimately need more than MaxUpscaleFactor.
        while ((width * factor < MinEngineDimension || height * factor < MinEngineDimension)
               && (long)width * (factor + 1) <= max
               && (long)height * (factor + 1) <= max)
        {
            factor++;
        }

        return factor;
    }

    private static Bitmap Upscale(Bitmap source, int factor)
    {
        var scaled = new Bitmap(source.Width * factor, source.Height * factor, PixelFormat.Format32bppArgb);
        try
        {
            using var g = Graphics.FromImage(scaled);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(source, new Rectangle(0, 0, scaled.Width, scaled.Height));
            return scaled;
        }
        catch
        {
            scaled.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Bitmap -> PNG -> SoftwareBitmap (SPEC 5.3). The PNG round-trip is not the
    /// bottleneck at these sizes and avoids hand-rolling pixel-format conversion.
    /// DataWriter is used rather than the AsBuffer/AsStream interop extensions because it
    /// is a plain WinRT projection that takes a byte[] directly.
    /// </summary>
    private static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(Bitmap bitmap)
    {
        using var memory = new MemoryStream();
        bitmap.Save(memory, ImageFormat.Png);

        var stream = new InMemoryRandomAccessStream();
        var writer = new DataWriter(stream);
        writer.WriteBytes(memory.ToArray());
        await writer.StoreAsync();
        await writer.FlushAsync();
        writer.DetachStream();

        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        return await decoder.GetSoftwareBitmapAsync();
    }
}
