using System.Drawing.Drawing2D;

namespace SelectAndRead;

/// <summary>
/// Full-screen overlay that shows the freeze frame and lets the user drag out a rectangle
/// (SPEC 2.2 - 2.3, 4).
///
/// Coordinate discipline, which is the whole point of this file: the window is positioned
/// with SetWindowPos in raw physical pixels and never autoscaled, so client coordinates,
/// mouse coordinates and freeze-frame pixel indices are all the same numbers. That holds
/// at any display scaling, which is what stops a drag on a 150%-scaled screen from
/// capturing the wrong region.
///
/// Everything drawn here is sized for very poor vision, which drives two choices that look
/// odd next to an ordinary selection tool. Nothing is dimmed until the drag starts, because
/// a wash over the whole screen hides the very thing the user is trying to aim at; and the
/// cursor is marked with black-and-white lines spanning the entire screen, because at low
/// acuity that is the only cue reliably findable without hunting. Black backing behind a
/// white core means one of the two always contrasts, whatever is underneath.
///
/// None of this can reach the OCR'd image: the crop is taken from the freeze frame, not
/// from anything painted here.
/// </summary>
internal sealed class SelectionOverlay : Form
{
    private const int MinimumSelection = 5;   // SPEC 2.3: smaller than this is a stray click
    private const int DimAlpha = 200;         // ~78% black wash, applied only while dragging

    private const int GuideOutline = 11;      // black backing band of the crosshair
    private const int GuideCore = 5;          // white core, centred within the backing
    private const int ReticleRadius = 44;     // pre-drag ring around the cursor
    private const int BorderBand = 8;         // each of the two selection strokes
    private const int BracketArm = 72;        // corner bracket length along each edge
    private const int BracketBand = 24;       // corner bracket thickness

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

        // Before the first paint, so the crosshair is never drawn at (0,0) first.
        SeedCursor();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyPhysicalBounds();

        // Again here, in case the pointer moved between handle creation and the window
        // becoming visible - no MouseMove is delivered for that interval.
        SeedCursor();
        Invalidate();

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

    /// <summary>
    /// The mouse may never move between the overlay opening and the click, so the crosshair
    /// cannot wait for the first MouseMove. The window is at (0,0), so screen and client
    /// coordinates are the same numbers (SPEC 4.1).
    /// </summary>
    private void SeedCursor()
    {
        if (Native.GetCursorPos(out var p)) _cursor = new Point(p.X, p.Y);
    }

    // --- Input ------------------------------------------------------------------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button == MouseButtons.Right) { Cancel("right-click"); return; }
        if (e.Button != MouseButtons.Left) return;

        _dragging = true;
        _anchor = e.Location;
        _cursor = e.Location;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        // Tracked before the drag too: the crosshair follows the pointer from the moment
        // the overlay appears.
        _cursor = e.Location;
        Invalidate();
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

    // --- Painting ---------------------------------------------------------------

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Fully covered by OnPaint; skipping this avoids a redundant fill.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        // Guarantee an exact 1:1 pixel copy of the freeze frame.
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.CompositingQuality = CompositingQuality.HighSpeed;

        var screen = new Rectangle(Point.Empty, _screenSize);
        g.DrawImage(_frame, screen, screen, GraphicsUnit.Pixel);

        if (_dragging)
        {
            var sel = CurrentSelection();
            DimAround(g, sel);
            DrawSelectionBorder(g, sel);
            DrawCorners(g, sel);
            DrawReadout(g, sel);
        }
        else
        {
            DrawReticle(g);
        }

        DrawGuides(g);   // last, so the guides are never buried
    }

    /// <summary>
    /// Dims the four bands around the selection rather than washing the whole screen and
    /// repainting the selection undimmed. Degrades correctly while the selection is still
    /// zero-sized: the bands then cover everything.
    /// </summary>
    private void DimAround(Graphics g, Rectangle sel)
    {
        var s = Rectangle.Intersect(sel, new Rectangle(Point.Empty, _screenSize));
        using var dim = new SolidBrush(Color.FromArgb(DimAlpha, 0, 0, 0));

        g.FillRectangle(dim, 0, 0, _screenSize.Width, s.Top);
        g.FillRectangle(dim, 0, s.Bottom, _screenSize.Width, _screenSize.Height - s.Bottom);
        g.FillRectangle(dim, 0, s.Top, s.Left, s.Height);
        g.FillRectangle(dim, s.Right, s.Top, _screenSize.Width - s.Right, s.Height);
    }

    /// <summary>
    /// Screen-spanning crosshair through the cursor. Both backing bands are laid down
    /// before either core, so the intersection stays clean. Filled rectangles rather than
    /// pens keep the edges crisp, and GDI+ clips at the screen edges for free.
    /// </summary>
    private void DrawGuides(Graphics g)
    {
        using var black = new SolidBrush(Color.Black);
        using var white = new SolidBrush(Color.White);

        g.FillRectangle(black, 0, _cursor.Y - GuideOutline / 2, _screenSize.Width, GuideOutline);
        g.FillRectangle(black, _cursor.X - GuideOutline / 2, 0, GuideOutline, _screenSize.Height);
        g.FillRectangle(white, 0, _cursor.Y - GuideCore / 2, _screenSize.Width, GuideCore);
        g.FillRectangle(white, _cursor.X - GuideCore / 2, 0, GuideCore, _screenSize.Height);
    }

    /// <summary>Ring marking the cursor before a drag begins, where the selection itself
    /// is not yet drawing any attention to it.</summary>
    private void DrawReticle(Graphics g)
    {
        var box = new Rectangle(
            _cursor.X - ReticleRadius, _cursor.Y - ReticleRadius,
            ReticleRadius * 2, ReticleRadius * 2);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var backing = new Pen(Color.Black, GuideOutline))
            g.DrawEllipse(backing, box);
        using (var core = new Pen(Color.White, GuideCore))
            g.DrawEllipse(core, box);
        g.SmoothingMode = SmoothingMode.Default;
    }

    /// <summary>
    /// Two strokes, both entirely outside the selection so none of the content the user
    /// picked is covered. Black innermost separates the white from light content; white
    /// outermost is what shows against the dim.
    /// </summary>
    private static void DrawSelectionBorder(Graphics g, Rectangle sel)
    {
        // Pen strokes are centred on the path, hence the half-width inflations.
        using var black = new Pen(Color.Black, BorderBand);
        using var white = new Pen(Color.White, BorderBand);

        g.DrawRectangle(black, Rectangle.Inflate(sel, BorderBand / 2, BorderBand / 2));
        g.DrawRectangle(white, Rectangle.Inflate(sel, BorderBand + BorderBand / 2, BorderBand + BorderBand / 2));
    }

    /// <summary>
    /// L-shaped brackets straddling the four corners, thicker and longer than the border
    /// itself. Once the edges blur into one another the corners are what remains legible.
    /// </summary>
    private static void DrawCorners(Graphics g, Rectangle sel)
    {
        // Clamped so opposite brackets cannot overrun each other on a small selection.
        var arm = Math.Min(BracketArm, Math.Min(sel.Width, sel.Height) / 2);
        if (arm <= 0) return;

        using var black = new SolidBrush(Color.Black);
        using var white = new SolidBrush(Color.White);

        DrawBracketBand(g, black, sel, arm, near: 0, far: BracketBand / 2);
        DrawBracketBand(g, white, sel, arm, near: BracketBand / 2, far: BracketBand);
    }

    /// <summary>
    /// One concentric layer of the corner brackets: eight bars forming four Ls, occupying
    /// the band between <paramref name="near"/> and <paramref name="far"/> pixels outside
    /// the selection.
    /// </summary>
    private static void DrawBracketBand(Graphics g, Brush brush, Rectangle sel, int arm, int near, int far)
    {
        int l = sel.Left, t = sel.Top, r = sel.Right, b = sel.Bottom;
        var band = far - near;
        var run = arm + far;

        g.FillRectangle(brush, l - far, t - far, run, band);      // top-left, horizontal
        g.FillRectangle(brush, l - far, t - far, band, run);      // top-left, vertical
        g.FillRectangle(brush, r - arm, t - far, run, band);      // top-right, horizontal
        g.FillRectangle(brush, r + near, t - far, band, run);     // top-right, vertical
        g.FillRectangle(brush, l - far, b + near, run, band);     // bottom-left, horizontal
        g.FillRectangle(brush, l - far, b - arm, band, run);      // bottom-left, vertical
        g.FillRectangle(brush, r - arm, b + near, run, band);     // bottom-right, horizontal
        g.FillRectangle(brush, r + near, b - arm, band, run);     // bottom-right, vertical
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
