using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace SelectAndRead;

/// <summary>
/// Marks the word being read aloud, on top of the live desktop (SPEC 16.4).
///
/// The mark is the word's own pixels inverted - a photo negative of the word and a little
/// bleed around it. That is what makes it a highlighter rather than a decoration: light text
/// on a dark page becomes dark text on a light block and the other way about, so the mark
/// contrasts whatever it lands on without owning a colour of its own, and the reader's eye
/// meets a solid block rather than a stroke to resolve.
///
/// The pixels come from the crop the reading was made from, handed over by
/// <see cref="SetSource"/> and inverted once up front. They cannot be read back off the
/// screen at each word: the window covers the word it marks, adjacent words overlap once the
/// bleed is added, and a capture would therefore pick the previous word's mark back up and
/// invert it a second time.
///
/// The coordinate discipline is SelectionOverlay's, for the same reason (SPEC 4.1): the
/// window is placed with SetWindowPos in raw physical pixels and never autoscaled. Word
/// boxes arrive in crop coordinates and the crop's origin is added here, so the pixels being
/// inverted and the place they are painted can never disagree.
/// </summary>
internal sealed class HighlightOverlay : Form
{
    /// <summary>
    /// How far the mark bleeds past the word. Without it the inversion stops at the ink, and
    /// the recogniser's boxes are tight to the ink rather than to the type - so a descender
    /// or an accent would hang outside the block that is meant to be holding the word.
    /// </summary>
    private const int Gap = 5;

    /// <summary>
    /// The crop the current reading was made from, already inverted. Retained rather than
    /// borrowed because a replay marks its words too, and by then the pipeline has disposed
    /// the crop it read from. This is the selection and not the screen, so it is not the
    /// freeze frame the pipeline goes out of its way not to hold - though a selection of the
    /// whole screen amounts to the same thing until the next reading replaces it.
    /// </summary>
    private Bitmap? _inverted;

    /// <summary>Where <see cref="_inverted"/> sits on screen.</summary>
    private Point _origin;

    /// <summary>The word being marked, in crop coordinates.</summary>
    private Rectangle _word = Rectangle.Empty;

    internal HighlightOverlay()
    {
        // SPEC 4.1: any WinForms autoscaling would introduce a second coordinate space.
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Black;
        Text = "Select and Read";

        // Buffered because the window moves and repaints on every word, and an unbuffered
        // move shows the empty client area for a frame - a black flash on each word. The
        // buffer is the size of one word, not of the screen.
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer, true);

        // Built here, on the UI thread, and not left until the first word arrives.
        // InvokeRequired answers false for a control with no handle yet, so a first call
        // from the speech timer would sail past the marshalling check and create the window
        // on a thread with no message pump - where it would never paint and never appear.
        _ = Handle;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x00000080;
            const int WS_EX_TRANSPARENT = 0x00000020;
            const int WS_EX_NOACTIVATE = 0x08000000;

            var cp = base.CreateParams;

            // Both of the last two are load-bearing. TRANSPARENT is the only thing letting a
            // click through to the app underneath, since the mark really does cover the word
            // rather than being shaped away from it. Without NOACTIVATE, showing the mark
            // steals focus from whatever the user is reading, and on a first show it would
            // take the foreground away mid-sentence.
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    /// <summary>
    /// Keeps the window from ever taking the foreground, which it would otherwise still do
    /// the first time it is shown.
    /// </summary>
    protected override bool ShowWithoutActivation => true;

    /// <summary>
    /// Hands over the pixels the marks will be cut from: the crop a reading was made from,
    /// and where on screen it came from. Safe to call from any thread.
    /// </summary>
    internal void SetSource(Bitmap crop, Point origin)
    {
        // Inverted here, on the calling thread and before any marshalling. The crop belongs
        // to the caller and is disposed as soon as the reading ends, so the copy has to be
        // taken while the call is still on the stack.
        Adopt(Invert(crop), origin);
    }

    private void Adopt(Bitmap inverted, Point origin)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Adopt(inverted, origin));
            return;
        }

        _inverted?.Dispose();
        _inverted = inverted;
        _origin = origin;
    }

    /// <summary>
    /// Marks the given word, in crop coordinates. Safe to call from any thread; a null or
    /// empty rectangle hides the mark, as does having no source to cut it from.
    /// </summary>
    internal void Show(Rectangle? word)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Show(word));
            return;
        }

        if (_inverted is null || word is not { Width: > 0, Height: > 0 } rect)
        {
            Clear();
            return;
        }

        _word = rect;

        if (Marked.IsEmpty)
        {
            Clear();
            return;
        }

        // Placed before being shown and again after, as SelectionOverlay does for the same
        // reason: making a Form visible applies its own cached bounds, which would put the
        // first word's mark somewhere else for a frame.
        Place();

        // WinForms has to agree that the window is visible. Showing it with SWP_SHOWWINDOW
        // alone leaves Control.Visible false, and Invalidate on a control WinForms believes
        // is hidden does nothing - so the mark would be a correctly placed, correctly sized,
        // permanently unpainted window.
        if (!Visible) Visible = true;

        Place();
        Invalidate();

        // Painted now rather than whenever the queue drains: the mark is chasing speech.
        Update();
    }

    internal void Clear()
    {
        if (InvokeRequired)
        {
            BeginInvoke(Clear);
            return;
        }

        _word = Rectangle.Empty;
        Visible = false;
    }

    /// <summary>
    /// What the mark covers, in crop coordinates: the word plus its bleed, clamped to the
    /// pixels there are. Clamping to the crop clamps to the screen too, since the crop was
    /// taken from it.
    /// </summary>
    private Rectangle Marked
    {
        get
        {
            if (_word.IsEmpty || _inverted is not { } source) return Rectangle.Empty;

            var marked = Rectangle.Inflate(_word, Gap, Gap);
            marked.Intersect(new Rectangle(Point.Empty, source.Size));
            return marked;
        }
    }

    private void Place()
    {
        var marked = Marked;

        Native.SetWindowPos(
            Handle, IntPtr.Zero,
            marked.X + _origin.X, marked.Y + _origin.Y, marked.Width, marked.Height,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Painted entirely in OnPaint, which covers the whole client area; the default fill
        // would only flicker on every word.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var marked = Marked;
        if (marked.IsEmpty || _inverted is not { } source) return;

        var g = e.Graphics;

        // The blit is 1:1, and these are what keep it that way: without them GDI+ is still
        // entitled to resample and to offset by half a pixel, which would soften the very
        // glyphs the mark is pointing at.
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        g.DrawImage(source, new Rectangle(Point.Empty, marked.Size), marked, GraphicsUnit.Pixel);
    }

    /// <summary>
    /// A photo negative of the whole crop, taken once so that marking a word is a single
    /// blit rather than a colour pass on a path that runs every word.
    /// </summary>
    private static Bitmap Invert(Bitmap crop)
    {
        // Format32bppRgb like the capture it came from: BitBlt leaves the alpha bytes at
        // zero, and a format with an alpha channel would read those as fully transparent
        // (SPEC 3).
        var inverted = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppRgb);

        try
        {
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(new ColorMatrix(
            [
                [-1f,  0f,  0f, 0f, 0f],
                [ 0f, -1f,  0f, 0f, 0f],
                [ 0f,  0f, -1f, 0f, 0f],
                [ 0f,  0f,  0f, 1f, 0f],
                [ 1f,  1f,  1f, 0f, 1f],
            ]));

            using var g = Graphics.FromImage(inverted);

            // As in OnPaint: the copy is 1:1 and must stay pixel for pixel, or every mark
            // inherits a half-pixel smear of the glyphs it is meant to be showing.
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;

            g.DrawImage(
                crop, new Rectangle(0, 0, crop.Width, crop.Height),
                0, 0, crop.Width, crop.Height, GraphicsUnit.Pixel, attributes);

            return inverted;
        }
        catch
        {
            inverted.Dispose();
            throw;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inverted?.Dispose();
        base.Dispose(disposing);
    }
}
