using System.Drawing.Imaging;

namespace SelectAndRead;

/// <summary>Primary-screen geometry and the freeze-frame capture (SPEC 2.2, 3).</summary>
internal static class ScreenCapture
{
    /// <summary>
    /// Primary monitor size in physical pixels. Its origin is always (0,0) by definition,
    /// which is why only a Size is needed: screen, capture and overlay coordinates are all
    /// the same numbers, with no offset anywhere.
    ///
    /// Deliberately uses GetSystemMetrics rather than Screen.Bounds: the latter is affected
    /// by the process DPI awareness context and is the classic source of mispositioned
    /// overlays (SPEC 4.2).
    /// </summary>
    internal static Size GetScreenSize() => new(
        Native.GetSystemMetrics(Native.SM_CXSCREEN),
        Native.GetSystemMetrics(Native.SM_CYSCREEN));

    /// <summary>
    /// Captures the primary monitor. This runs before the overlay is shown so the overlay
    /// can never contaminate what is recognised, and the scene cannot change mid-drag
    /// (SPEC 2.2).
    /// </summary>
    internal static Bitmap CaptureScreen(Size size)
    {
        // Format32bppRgb, NOT Format32bppArgb. BitBlt copies only the colour channels and
        // leaves the alpha bytes at zero, so an ARGB bitmap comes back fully transparent:
        // DrawImage then paints nothing and the overlay renders as solid black. The Rgb
        // format has no alpha channel, so every pixel is treated as opaque.
        var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppRgb);
        try
        {
            using var g = Graphics.FromImage(bmp);

            // CAPTUREBLT so layered windows are included. This goes through BitBlt rather
            // than Graphics.CopyFromScreen because CopyPixelOperation is not a [Flags]
            // enum - OR-ing the two constants produces a value the managed overload
            // rejects with ArgumentException at runtime.
            var screenDc = Native.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) throw new InvalidOperationException("No screen DC.");

            var targetDc = g.GetHdc();
            try
            {
                if (!Native.BitBlt(
                        targetDc, 0, 0, size.Width, size.Height,
                        screenDc, 0, 0, Native.SRCCOPY | Native.CAPTUREBLT))
                {
                    throw new InvalidOperationException("Screen capture failed.");
                }
            }
            finally
            {
                g.ReleaseHdc(targetDc);
                Native.ReleaseDC(IntPtr.Zero, screenDc);
            }

            return bmp;
        }
        catch
        {
            bmp.Dispose();
            throw;
        }
    }

    /// <summary>Crops in freeze-frame coordinates, clamped to the image.</summary>
    internal static Bitmap Crop(Bitmap source, Rectangle rect)
    {
        rect.Intersect(new Rectangle(0, 0, source.Width, source.Height));
        if (rect.Width <= 0 || rect.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(rect), "Selection is outside the capture.");

        return source.Clone(rect, source.PixelFormat);
    }

    /// <summary>
    /// Cheap uniform-colour test on a sampled grid. Protected/DRM surfaces capture as
    /// solid black (SPEC 3.1), and reporting "capture failed" for that is far more
    /// diagnostic than the "no text found" the OCR stage would otherwise produce.
    /// </summary>
    internal static bool LooksBlank(Bitmap bmp)
    {
        const int steps = 8;
        var first = bmp.GetPixel(0, 0);

        for (var yi = 0; yi < steps; yi++)
        {
            var y = Math.Min(bmp.Height - 1, yi * bmp.Height / steps);
            for (var xi = 0; xi < steps; xi++)
            {
                var x = Math.Min(bmp.Width - 1, xi * bmp.Width / steps);
                if (bmp.GetPixel(x, y) != first) return false;
            }
        }
        return true;
    }
}
