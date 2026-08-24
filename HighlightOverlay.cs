namespace SelectAndRead;

/// <summary>
/// Marks the word being read aloud, on top of the live desktop (SPEC 16.4).
///
/// A box around the word, plus screen-spanning crosshair lines centred on it - the same two
/// cues SelectionOverlay gives the cursor, for the same reason. At low acuity a box a few
/// hundred pixels away is not findable by scanning; lines that run the width and height of
/// the screen are, and they lead the eye to the box.
///
/// The window covers the screen but is shaped with SetWindowRgn down to just those strokes,
/// so it has no pixels anywhere else - most importantly none over the word itself, and none
/// over the rest of the desktop. That is stronger than painting transparently: there is
/// nothing to see through, nothing to hit-test, and click-through comes for free because the
/// removed area is not part of the window at all.
///
/// The coordinate discipline is SelectionOverlay's, for the same reason (SPEC 4.1): the
/// window is placed with SetWindowPos in raw physical pixels and never autoscaled, so the
/// rectangle handed to <see cref="Show"/>, the client coordinates it is painted in, and the
/// screen are all the same numbers.
/// </summary>
internal sealed class HighlightOverlay : Form
{
    /// <summary>
    /// Every stroke is a white core on a black backing, so one of the two contrasts whatever
    /// is underneath - white sandwiched in black rather than merely paired with it, which is
    /// what keeps it readable over light content, dark content and a photograph alike. These
    /// are SelectionOverlay's guide widths, because this is the same mark for the same eyes.
    /// </summary>
    private const int Stroke = 11;
    private const int Core = 5;

    /// <summary>Black either side of the core. Derived so the core stays centred.</summary>
    private const int Backing = (Stroke - Core) / 2;

    /// <summary>
    /// Clear space between the word and the box. Without it the stroke sits against the
    /// glyphs, and a descender or an accent reads as part of the mark rather than as part of
    /// the letter - at which point the mark is interfering with the very thing it is pointing
    /// at. It also gives the recogniser's own box a little slack, since the bounds it reports
    /// are tight to the ink rather than to the type.
    /// </summary>
    private const int Gap = 5;

    private Rectangle _word = Rectangle.Empty;
    private Size _screen;

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

        // Deliberately no OptimizedDoubleBuffer. The window is screen-sized, so a back buffer
        // would be tens of megabytes reallocated on every word, to composite the few thousand
        // pixels the region actually admits. The region is what keeps the painting cheap.
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);

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

            // NOACTIVATE is the load-bearing one: without it, showing the mark steals focus
            // from whatever the user is reading, and on a first show it would take the
            // foreground away mid-sentence.
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    /// <summary>
    /// Keeps the window from ever taking the foreground, which the shaped window would
    /// otherwise still do the first time it is shown.
    /// </summary>
    protected override bool ShowWithoutActivation => true;

    /// <summary>
    /// Marks the given word, in screen pixels. Safe to call from any thread; a null or empty
    /// rectangle hides the mark.
    /// </summary>
    internal void Show(Rectangle? word)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Show(word));
            return;
        }

        if (word is not { Width: > 0, Height: > 0 } rect)
        {
            Clear();
            return;
        }

        _word = rect;

        // Re-read rather than cached: a resolution change between readings would otherwise
        // leave the crosshair stopping short of, or running past, the new screen.
        _screen = ScreenCapture.GetScreenSize();

        // Placed before being shown and again after, as SelectionOverlay does for the same
        // reason: making a Form visible applies its own cached bounds, which would put the
        // first word's mark somewhere else for a frame.
        Place();

        // WinForms has to agree that the window is visible. Showing it with SWP_SHOWWINDOW
        // alone leaves Control.Visible false, and Invalidate on a control WinForms believes
        // is hidden does nothing - so the mark would be a correctly placed, correctly shaped,
        // permanently unpainted window.
        if (!Visible) Visible = true;

        Place();
        ApplyShape();

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

    private void Place() => Native.SetWindowPos(
        Handle, IntPtr.Zero, 0, 0, _screen.Width, _screen.Height,
        Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);

    /// <summary>The clear space the box is held off the word by, and the hole in the shape.</summary>
    private Rectangle Clearance => Rectangle.Inflate(_word, Gap, Gap);

    /// <summary>The box's outer edge - where the crosshair lines stop.</summary>
    private Rectangle Surround => Rectangle.Inflate(_word, Gap + Stroke, Gap + Stroke);

    private Point Centre => new(_word.X + _word.Width / 2, _word.Y + _word.Height / 2);

    /// <summary>
    /// Cuts the window down to the box and the two lines: a ring around the word, plus a
    /// full-width and a full-height band that both stop at the box rather than crossing it.
    ///
    /// Everything else is removed, so the window covers the screen without covering anything
    /// on it. The window owns the region once SetWindowRgn succeeds, hence the delete only on
    /// failure.
    /// </summary>
    private void ApplyShape()
    {
        var surround = Surround;
        var centre = Centre;

        var shape = RectRegion(surround);
        var hole = RectRegion(Clearance);
        var box = RectRegion(surround);

        try
        {
            // The box: a ring around the clearance, so neither the word nor the space held
            // clear around it is part of the window.
            Native.CombineRgn(shape, shape, hole, Native.RGN_DIFF);

            AddBand(shape, box, new Rectangle(
                0, centre.Y - Stroke / 2, _screen.Width, Stroke));

            AddBand(shape, box, new Rectangle(
                centre.X - Stroke / 2, 0, Stroke, _screen.Height));

            if (Native.SetWindowRgn(Handle, shape, bRedraw: true) == 0)
            {
                Native.DeleteObject(shape);
            }
        }
        finally
        {
            Native.DeleteObject(hole);
            Native.DeleteObject(box);
        }
    }

    /// <summary>Adds one crosshair band, minus the box, to the shape being built.</summary>
    private static void AddBand(IntPtr shape, IntPtr box, Rectangle band)
    {
        var region = RectRegion(band);

        try
        {
            Native.CombineRgn(region, region, box, Native.RGN_DIFF);
            Native.CombineRgn(shape, shape, region, Native.RGN_OR);
        }
        finally
        {
            Native.DeleteObject(region);
        }
    }

    private static IntPtr RectRegion(Rectangle r) =>
        Native.CreateRectRgn(r.Left, r.Top, r.Right, r.Bottom);

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Painted entirely in OnPaint; the default fill would flicker on every word.
    }

    /// <summary>
    /// Black everywhere, then the white cores laid back over it. Nothing here masks the word
    /// or the desktop: the window region has already removed both, so a fill that covers them
    /// is clipped away before it reaches the screen. That is what lets the backing be one
    /// Clear and each core one or two rectangles, instead of bands counted out individually.
    /// </summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        if (_word.IsEmpty) return;

        var g = e.Graphics;
        g.Clear(Color.Black);

        using var white = new SolidBrush(Color.White);
        using var black = new SolidBrush(Color.Black);

        // The line cores, held out of the box so the two marks stay legible where they meet.
        var clip = g.Save();
        g.ExcludeClip(Surround);
        g.FillRectangle(white, 0, Centre.Y - Core / 2, _screen.Width, Core);
        g.FillRectangle(white, Centre.X - Core / 2, 0, Core, _screen.Height);
        g.Restore(clip);

        // The box core: white out to the core's outer edge, then black back over the inner
        // backing band. The word and its clearance are outside the region, so the second fill
        // only lands on the innermost band of the ring.
        g.FillRectangle(white, Rectangle.Inflate(
            _word, Gap + Backing + Core, Gap + Backing + Core));
        g.FillRectangle(black, Rectangle.Inflate(
            _word, Gap + Backing, Gap + Backing));
    }
}
