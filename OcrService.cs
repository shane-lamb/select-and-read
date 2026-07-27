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

    /// <summary>
    /// Glyph height at or above which upscaling is skipped entirely.
    ///
    /// Calibrated against two measured cases that bracket it: a screen capture of Notepad
    /// prose at 20px glyphs is recognised exactly when upscaled and has four errors at
    /// native scale, while a desktop icon label at 27px glyphs is correct at native scale
    /// and misreads "net10.0" as "netl 0.0" when enlarged. The crossover lies between.
    /// </summary>
    private const double GlyphHeightCeiling = 25;

    /// <summary>
    /// Glyph height that small text is scaled towards. Deliberately well above the
    /// ceiling: the same 20px capture is exact at 4x but has three errors at 2x, so text
    /// worth enlarging is worth enlarging properly rather than nudging.
    /// </summary>
    private const double TargetGlyphHeight = 80;

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

    /// <summary>What the recogniser measured and decided. Surfaced by --ocr-file, because
    /// "the OCR was wrong" is not actionable without knowing the glyph size it saw and the
    /// scale it picked.</summary>
    internal sealed record Diagnostics(double MedianGlyphHeight, int Scale, int Words);

    /// <summary>Recognises the crop and returns speech-ready text (may be empty).</summary>
    internal async Task<string> RecognizeAsync(Bitmap crop, bool upscale) =>
        (await RecognizeDetailedAsync(crop, upscale)).Text;

    internal async Task<(string Text, Diagnostics Info)> RecognizeDetailedAsync(Bitmap crop, bool upscale)
    {
        // A crop below the engine's floor cannot be recognised at all, so it must be
        // enlarged before anything else can be measured.
        var minimum = MinimumViableScale(crop.Width, crop.Height);
        if (minimum > 1)
        {
            using var enlarged = Upscale(crop, minimum);
            var forced = await RunAsync(enlarged);
            return (Clean(forced), new Diagnostics(MedianGlyphHeight(forced), minimum, WordCount(forced)));
        }

        // Pass one, at native scale. Besides being the answer for text that is already
        // large enough, this is what makes the glyph height measurable.
        var native = await RunAsync(crop);
        var median = MedianGlyphHeight(native);

        if (!upscale) return (Clean(native), new Diagnostics(median, 1, WordCount(native)));

        var factor = ChooseScale(native, crop.Width, crop.Height);
        if (factor <= 1) return (Clean(native), new Diagnostics(median, 1, WordCount(native)));

        using var scaled = Upscale(crop, factor);
        var result = await RunAsync(scaled);
        return (Clean(result), new Diagnostics(median, factor, WordCount(result)));
    }

    private static int WordCount(OcrResult result) => result.Lines.Sum(l => l.Words.Count);

    private static double MedianGlyphHeight(OcrResult result)
    {
        var heights = result.Lines
            .SelectMany(line => line.Words)
            .Select(word => word.BoundingRect.Height)
            .Where(h => h > 0)
            .OrderBy(h => h)
            .ToList();

        return heights.Count == 0 ? 0 : heights[heights.Count / 2];
    }

    private static string Clean(OcrResult result) =>
        TextCleaner.Clean(result.Lines.Select(l => l.Text));

    private async Task<OcrResult> RunAsync(Bitmap bitmap)
    {
        var softwareBitmap = await ToSoftwareBitmapAsync(bitmap);
        using (softwareBitmap) return await _engine.RecognizeAsync(softwareBitmap);
    }

    /// <summary>
    /// SPEC 5.2. Windows OCR is weak on small UI text, so upscaling it helps - but only
    /// when the text really is small.
    ///
    /// The factor is derived from the median glyph height reported by the native-scale
    /// pass, never from the crop's own dimensions. An earlier version used the crop size
    /// as a proxy for text size, which is simply the wrong measurement: a 205x145 capture
    /// of a desktop icon label got a 4x upscale even though its glyphs were already ~30px,
    /// and the enlargement smeared the "1" in "net10.0" into an "l". Crop dimensions say
    /// nothing about how big the text inside them is.
    /// </summary>
    internal static int ChooseScale(OcrResult native, int width, int height)
    {
        var median = MedianGlyphHeight(native);

        // Nothing legible at native scale: the text may be too small to detect at all, so
        // fall back to enlarging by a fixed amount and trying again.
        if (median <= 0) return CapToEngineLimit(MaxUpscaleFactor, width, height);

        if (median >= GlyphHeightCeiling) return 1;

        var factor = (int)Math.Round(TargetGlyphHeight / median);
        return CapToEngineLimit(Math.Clamp(factor, 1, MaxUpscaleFactor), width, height);
    }

    /// <summary>Smallest factor that lifts a tiny crop over the engine's minimum size.</summary>
    private static int MinimumViableScale(int width, int height)
    {
        var max = (int)Math.Min(int.MaxValue, OcrEngine.MaxImageDimension);
        var factor = 1;

        while ((width * factor < MinEngineDimension || height * factor < MinEngineDimension)
               && (long)width * (factor + 1) <= max
               && (long)height * (factor + 1) <= max)
        {
            factor++;
        }

        return factor;
    }

    private static int CapToEngineLimit(int factor, int width, int height)
    {
        var max = (int)Math.Min(int.MaxValue, OcrEngine.MaxImageDimension);
        while (factor > 1 && ((long)width * factor > max || (long)height * factor > max))
            factor--;
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
