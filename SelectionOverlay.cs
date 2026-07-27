using System.Drawing.Drawing2D;

namespace SelectAndRead;

/// <summary>
/// Full-screen overlay that shows the freeze frame dimmed and lets the user drag out a
/// rectangle (SPEC 2.2 - 2.3, 4).
///
/// Coordinate discipline, which is the whole point of this file: the window is positioned
/// with SetWindowPos in raw physical pixels and never autoscaled, so client coordinates,
/// mouse coordinates and freeze-frame pixel indices are all the same numbers. That holds
/// at any display scaling, which is what stops a drag on a 150%-scaled screen from
/// capturing the wrong region.
/// </summary>
internal sealed class SelectionOverlay : Form
{
    private const int MinimumSelection = 5;   // SPEC 2.3: smaller than this is a stray click
    private const int DimAlpha = 115;         // ~45% black wash
    private static readonly Size ReadoutSize = new(132, 26);

    private readonly Bitmap _frame;           // not owned; the caller disposes it
    private readonly Size _screenSize;

    /// <summary>
    /// Makes ESC work even when the overlay does not hold focus. See the note where
    /// OnDeactivate would be for why an unfocused overlay is normal rather than
    /// exceptional - without this hook, such an overlay could not be dismissed at all.
    /// </summary>
    private readonly EscapeWatcher _escape = new();

    private bool _dragging;
    private bool _closing;
    private Point _anchor;
    private Point _cursor;
    private Rectangle? _lastDecoration;

    /// <summary>Selection in freeze-frame coordinates, or null if cancelled.</summary>
    internal Rectangle? Selection { get; private set; }

    /// <summary>Why the selection was cancelled; null when one was made. Surfaced by the
    /// --capture-to debug mode, where "it just cancelled" is otherwise undiagnosable.</summary>
    internal string? CancelReason { get; private set; }

    internal SelectionOverlay(Bitmap frame, Size screenSize)
    {
        _frame = frame;
        _screenSize = screenSize;

        // SPEC 4.1: any WinForms autoscaling would introduce a second coordinate space.
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        Cursor = Cursors.Cross;
        BackColor = Color.Black;
        Text = "Select and Read";

        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer, true);

        _escape.EscapePressed += () => Cancel("escape");
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x00000080;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW;   // keep the overlay out of Alt+Tab
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyPhysicalBounds();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyPhysicalBounds();

        // Best effort only. Windows' foreground lock frequently refuses this when the
        // hotkey arrives while another application is active, so nothing may depend on
        // it succeeding.
        Native.SetForegroundWindow(Handle);
        Activate();

        _escape.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _escape.Stop();
        base.OnFormClosed(e);
    }

    /// <summary>
    /// Positions the window with SetWindowPos, which under Per-Monitor-V2 operates in
    /// physical pixels. Setting the Bounds property instead would route through WinForms'
    /// DPI scaling and reintroduce a second coordinate space.
    /// </summary>
    private void ApplyPhysicalBounds() => Native.SetWindowPos(
        Handle, IntPtr.Zero,
        0, 0, _screenSize.Width, _screenSize.Height,
        Native.SWP_NOZORDER | Native.SWP_SHOWWINDOW);

    // --- Input ------------------------------------------------------------------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button == MouseButtons.Right) { Cancel("right-click"); return; }
        if (e.Button != MouseButtons.Left) return;

        _dragging = true;
        _anchor = e.Location;
        _cursor = e.Location;
        InvalidateDecoration();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;

        _cursor = e.Location;
        InvalidateDecoration();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging || e.Button != MouseButtons.Left) return;

        if (_closing) return;
        _dragging = false;
        var rect = CurrentSelection();

        if (rect.Width < MinimumSelection || rect.Height < MinimumSelection)
        {
            Cancel($"selection too small ({rect.Width}x{rect.Height})");
            return;
        }

        Selection = rect;
        _closing = true;
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Cancel("escape");
    }

    // Deliberately no OnDeactivate handler.
    //
    // An earlier revision cancelled the selection when the overlay lost activation, on the
    // theory that Alt+Tab should dismiss it. Testing on Windows 11 showed that the overlay
    // routinely activates and is then immediately deactivated by the foreground lock
    // handing focus back to the previous app - so the selection cancelled itself before
    // the user could draw anything. Any notification stealing focus would do the same.
    //
    // Losing focus is therefore treated as normal. What matters is that the overlay always
    // remains escapable, which the low-level ESC hook guarantees regardless of focus.

    private void Cancel(string reason)
    {
        if (_closing) return;
        _closing = true;

        Selection = null;
        CancelReason = reason;
        DialogResult = DialogResult.Cancel;
        Close();
    }

    // --- Geometry ---------------------------------------------------------------

    /// <summary>Normalised so dragging in any direction works.</summary>
    private Rectangle CurrentSelection() => Rectangle.FromLTRB(
        Math.Min(_anchor.X, _cursor.X), Math.Min(_anchor.Y, _cursor.Y),
        Math.Max(_anchor.X, _cursor.X), Math.Max(_anchor.Y, _cursor.Y));

    private Rectangle ReadoutBounds()
    {
        // Below-right of the cursor, flipped near the edges so it stays on screen.
        var x = _cursor.X + 16;
        var y = _cursor.Y + 20;
        if (x + ReadoutSize.Width > ClientSize.Width) x = _cursor.X - 16 - ReadoutSize.Width;
        if (y + ReadoutSize.Height > ClientSize.Height) y = _cursor.Y - 20 - ReadoutSize.Height;
        return new Rectangle(x, y, ReadoutSize.Width, ReadoutSize.Height);
    }

    /// <summary>Everything the overlay draws on top of the dimmed frame.</summary>
    private Rectangle DecorationBounds()
    {
        var sel = CurrentSelection();
        sel.Inflate(4, 4);   // covers the border stroke
        return Rectangle.Union(sel, ReadoutBounds());
    }

    /// <summary>
    /// SPEC 4.3: repainting the whole screen per mouse-move is visibly laggy, so only the
    /// union of the previous and current decorations is invalidated.
    /// </summary>
    private void InvalidateDecoration()
    {
        var current = DecorationBounds();
        Invalidate(_lastDecoration is { } previous ? Rectangle.Union(previous, current) : current);
        _lastDecoration = current;
    }

    // --- Painting ---------------------------------------------------------------

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Fully covered by OnPaint; skipping this avoids a redundant fill.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var clip = e.ClipRectangle;

        // Guarantee an exact 1:1 pixel copy of the freeze frame.
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.CompositingQuality = CompositingQuality.HighSpeed;

        // Only the invalidated slice is drawn, both for the frame and the wash.
        g.DrawImage(_frame, clip, clip, GraphicsUnit.Pixel);

        using (var dim = new SolidBrush(Color.FromArgb(DimAlpha, 0, 0, 0)))
            g.FillRectangle(dim, clip);

        if (!_dragging) return;

        var sel = CurrentSelection();
        if (sel.Width <= 0 || sel.Height <= 0) return;

        // Repaint the selected region undimmed, so it reads as a hole in the wash.
        var visible = Rectangle.Intersect(sel, clip);
        if (visible is { Width: > 0, Height: > 0 })
            g.DrawImage(_frame, visible, visible, GraphicsUnit.Pixel);

        using (var pen = new Pen(Color.FromArgb(255, 90, 160, 255)))
            g.DrawRectangle(pen, sel.X, sel.Y, Math.Max(0, sel.Width - 1), Math.Max(0, sel.Height - 1));

        DrawReadout(g, sel);
    }

    private void DrawReadout(Graphics g, Rectangle sel)
    {
        var box = ReadoutBounds();
        var text = $"{sel.Width} × {sel.Height}";

        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var back = new SolidBrush(Color.FromArgb(220, 20, 20, 20)))
            g.FillRectangle(back, box);
        using (var edge = new Pen(Color.FromArgb(120, 255, 255, 255)))
            g.DrawRectangle(edge, box.X, box.Y, box.Width - 1, box.Height - 1);

        using var font = new Font(SystemFonts.DefaultFont.FontFamily, 10f);
        using var fore = new SolidBrush(Color.White);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        g.DrawString(text, font, fore, box, format);
        g.SmoothingMode = SmoothingMode.Default;
    }
}
