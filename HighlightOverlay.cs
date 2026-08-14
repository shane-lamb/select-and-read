namespace SelectAndRead;

/// <summary>
/// Marks the word being read aloud, on top of the live desktop (SPEC 16.4).
///
/// It is a window shaped like a rectangular ring: the middle is cut out with SetWindowRgn,
/// so the window has no pixels whatsoever over the word it surrounds. That is what makes it
/// safe on top of a live screen - there is nothing to see through, nothing to hit-test, and
/// nothing that can obscure the text a user with poor vision is trying to follow. It is also
/// why this is a separate class from SelectionOverlay, which is opaque, modal and paints an
/// entire freeze frame.
///
/// The coordinate discipline is SelectionOverlay's, for the same reason (SPEC 4.1): the
/// window is placed with SetWindowPos in raw physical pixels and never autoscaled, so the
/// rectangle handed to <see cref="Show"/> is in the same space as the screen, the freeze
/// frame and the crop.
/// </summary>
internal sealed class HighlightOverlay : Form
{
    /// <summary>
    /// Thickness of each of the two strokes. As in SelectionOverlay, every stroke is black
    /// paired with white so one of the two contrasts whatever is underneath, and both sit
    /// entirely outside the word - a mark that covered the text it was pointing at would
    /// defeat the purpose. Hardcoded for the same reason the selection border is: there is no
    /// user of this app who wants it thinner.
    /// </summary>
    private const int OuterBand = 6;
    private const int InnerBand = 6;

    /// <summary>Total clearance between the word and the outside of the window.</summary>
    private const int Clearance = OuterBand + InnerBand;

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

            // NOACTIVATE is the load-bearing one: without it, showing the highlight steals
            // focus from whatever the user is reading, and on a first show it would take the
            // foreground away mid-sentence.
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    /// <summary>
    /// Keeps the window from ever taking the foreground, which the shaped, disabled window
    /// would otherwise still do the first time it is shown.
    /// </summary>
    protected override bool ShowWithoutActivation => true;

    /// <summary>
    /// Surrounds the given word, in screen pixels. Safe to call from any thread and in any
    /// order relative to <see cref="Clear"/>; a null or empty rectangle hides the mark.
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

        var outer = Rectangle.Inflate(rect, Clearance, Clearance);

        // Placed before being shown and again after, as SelectionOverlay does for the same
        // reason: making a Form visible applies its own cached bounds, which would put the
        // first word's mark somewhere else for a frame.
        Place(outer);

        // WinForms has to agree that the window is visible. Showing it with SWP_SHOWWINDOW
        // alone leaves Control.Visible false, and Invalidate on a control WinForms believes
        // is hidden does nothing - so the mark would be a correctly placed, correctly shaped,
        // permanently unpainted window.
        if (!Visible) Visible = true;

        Place(outer);
        ApplyRing(outer.Size);

        Invalidate();

        // Painted now rather than whenever the queue drains: the mark is chasing speech.
        Update();
    }

    private void Place(Rectangle outer) => Native.SetWindowPos(
        Handle, IntPtr.Zero, outer.X, outer.Y, outer.Width, outer.Height,
        Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);

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
    /// Cuts the word-sized hole out of the window. The region is in client coordinates, and
    /// the window owns it once SetWindowRgn succeeds - hence the delete only on failure.
    /// </summary>
    private void ApplyRing(Size outer)
    {
        var ring = Native.CreateRectRgn(0, 0, outer.Width, outer.Height);
        var hole = Native.CreateRectRgn(
            Clearance, Clearance, outer.Width - Clearance, outer.Height - Clearance);

        try
        {
            Native.CombineRgn(ring, ring, hole, Native.RGN_DIFF);

            if (Native.SetWindowRgn(Handle, ring, bRedraw: true) == 0)
            {
                Native.DeleteObject(ring);
            }
        }
        finally
        {
            Native.DeleteObject(hole);
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Painted entirely in OnPaint; the default fill would flicker on every word.
    }

    /// <summary>
    /// Two rectangles, not four bands each. The window region has already removed the middle,
    /// so a fill that covers the word is clipped away before it reaches the screen - which
    /// makes the black backing one rectangle and the white core one more.
    /// </summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        if (_word.IsEmpty) return;

        e.Graphics.Clear(Color.Black);

        using var core = new SolidBrush(Color.White);
        e.Graphics.FillRectangle(
            core,
            OuterBand, OuterBand,
            _word.Width + InnerBand * 2, _word.Height + InnerBand * 2);
    }
}
