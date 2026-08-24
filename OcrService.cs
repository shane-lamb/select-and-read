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

    /// <summary>
    /// Cleaned text plus, for each word in it, the box it was recognised in - already
    /// divided back down to crop pixels, so the caller never has to know a pass ran enlarged
    /// (SPEC 16.2).
    /// </summary>
    internal sealed record Recognition(
        string Text, IReadOnlyList<TextCleaner.Span> Spans, Diagnostics Info);

    /// <summary>Recognises the crop and returns speech-ready text (may be empty).</summary>
    internal async Task<string> RecognizeAsync(Bitmap crop, bool upscale) =>
        (await RecognizeDetailedAsync(crop, upscale)).Text;

    internal async Task<Recognition> RecognizeDetailedAsync(Bitmap crop, bool upscale)
    {
        // A crop below the engine's floor cannot be recognised at all, so it must be
        // enlarged before anything else can be measured.
        var minimum = MinimumViableScale(crop.Width, crop.Height);
        if (minimum > 1)
        {
            using var enlarged = Upscale(crop, minimum);
            var forced = await RunAsync(enlarged);
            return Recognise(forced, MedianGlyphHeight(forced), minimum);
        }

        // Pass one, at native scale. Besides being the answer for text that is already
        // large enough, this is what makes the glyph height measurable.
        var native = await RunAsync(crop);
        var median = MedianGlyphHeight(native);

        if (!upscale) return Recognise(native, median, 1);

        var factor = ChooseScale(native, crop.Width, crop.Height);
        if (factor <= 1) return Recognise(native, median, 1);

        using var scaled = Upscale(crop, factor);
        var result = await RunAsync(scaled);
        return Recognise(result, median, factor);
    }

    private static Recognition Recognise(OcrResult result, double median, int scale)
    {
        var cleaned = Clean(result, scale);
        return new Recognition(
            cleaned.Text, cleaned.Spans, new Diagnostics(median, scale, WordCount(result)));
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

    /// <summary>
    /// Cleans the recognised words rather than the recognised line strings, which is what
    /// keeps each word's box attached to the text it produced. The engine builds OcrLine.Text
    /// by joining its words with single spaces, so the spoken result is unchanged - the
    /// fixtures in tests/fixtures are what hold that to be true.
    /// </summary>
    private static TextCleaner.Result Clean(OcrResult result, int scale) =>
        TextCleaner.CleanWords(result.Lines.Select(line => (IReadOnlyList<TextCleaner.Word>)
            line.Words
                .Select(word => new TextCleaner.Word(word.Text, Descale(word.BoundingRect, scale)))
                .ToList()));

    /// <summary>
    /// Maps a box from the pass that produced it back to crop pixels. A pass that ran at 4x
    /// reports boxes four times too large and four times too far from the origin, and the
    /// crop is itself positioned in screen coordinates by the caller - so an un-divided box
    /// would put the highlight somewhere off down and to the right of the actual word.
    /// </summary>
    private static Rectangle Descale(Windows.Foundation.Rect rect, int scale) => new(
        (int)Math.Round(rect.X / scale),
        (int)Math.Round(rect.Y / scale),
        (int)Math.Round(rect.Width / scale),
        (int)Math.Round(rect.Height / scale));

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
    /// pass, never from the crop's own dimensions. Crop size is the tempting proxy and is
    /// simply the wrong measurement: it hands a 205x145 capture of a desktop icon label a 4x
    /// upscale even though its glyphs are already ~30px, and that enlargement smears the "1"
    /// in "net10.0" into an "l". Crop dimensions say nothing about how big the text inside
    /// them is.
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
