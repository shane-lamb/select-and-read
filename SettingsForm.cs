namespace SelectAndRead;

/// <summary>
/// Settings dialog (SPEC 10). Built in code; no designer file.
///
/// Every dimension here is derived from the current font rather than hardcoded in pixels,
/// and the form auto-sizes to its content. Absolute SetBounds calls look fine at the default
/// font and make the dialog unusable the moment the user raises the system text size -
/// labels clipped to a single character, rows overlapping, and the buttons cut off below the
/// client area.
///
/// Note this is the opposite choice to SelectionOverlay, which pins AutoScaleMode to None
/// on purpose. That form must stay in raw physical pixels because its coordinates index
/// the freeze frame (SPEC 4.1); an ordinary dialog has no such constraint and should scale
/// with the user's font like any other Windows dialog.
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly HotkeyBox _capture;
    private readonly HotkeyBox _playback;
    private readonly ComboBox _language = new();
    private readonly ComboBox _voice = new();
    private readonly TrackBar _rate = new();
    private readonly Label _rateLabel = new();
    private readonly CheckBox _upscale = new();
    private readonly CheckBox _clipboard = new();
    private readonly CheckBox _startWithWindows = new();

    private readonly CheckBox _useCloud = new();
    private readonly TextBox _apiKey = new();
    private readonly ComboBox _cloudModel = new();
    private readonly ComboBox _cloudVoice = new();
    private readonly CheckBox _overridePrompt = new();
    private readonly TextBox _cloudPrompt = new();
    private string _customPrompt = string.Empty;

    private readonly TableLayoutPanel _grid = new();
    private Panel? _scroll;
    private Control? _buttons;
    private readonly int _inputWidth;
    private readonly int _pad;

    /// <summary>
    /// Text that wraps rather than widening the dialog, with the width it asks for when
    /// there is room. Re-bounded to the viewport whenever that is the smaller of the two.
    /// </summary>
    private readonly List<(Control Control, int NaturalWidth)> _wrapping = new();

    /// <summary>Populated when the dialog returns OK.</summary>
    internal Config Result { get; private set; }

    /// <summary>
    /// The API key as typed, for the caller to hand to ApiKeyStore. Kept off Config on
    /// purpose - Config is serialised to plain-text config.json.
    /// </summary>
    internal string? ApiKey { get; private set; }

    internal SettingsForm(Config config)
    {
        Result = config;

        Text = "Select and Read — Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;

        // Scale with the system font. The size still comes from the content, but it is
        // computed in OnLoad and clamped to the desktop rather than left to Form.AutoSize:
        // AutoSize has no upper bound, so at this row count it grows the dialog straight
        // past the bottom of the screen.
        AutoScaleMode = AutoScaleMode.Font;

        // Font-relative metrics: these track the user's text size instead of fighting it.
        _inputWidth = Font.Height * 14;
        _pad = Font.Height / 2;

        _capture = new HotkeyBox(config.Capture);
        _playback = new HotkeyBox(config.Playback);

        _grid.ColumnCount = 2;
        _grid.AutoSize = true;
        _grid.AutoSizeMode = AutoSizeMode.GrowAndShrink;

        _grid.Padding = new Padding(_pad);
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // captions: as wide as their text
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddRow("Capture hotkey", _capture);
        AddRow("Pause hotkey", _playback);
        AddHint(
            "The pause hotkey resumes a paused reading, and replays the last one from the " +
            "beginning once it has finished. Hold Ctrl, Shift or Alt and press a key while focused on the input box to set a custom hotkey.");

        BuildLanguageCombo(config);
        AddRow("OCR language", _language);

        BuildVoiceCombo(config);
        AddRow("Voice", _voice);

        BuildRateControls(config);
        AddRow("Speaking rate", BuildRateRow());

        AddCheck(_upscale, "Upscale small text before OCR (improves accuracy)", config.UpscaleBeforeOcr);
        AddCheck(_clipboard, "Copy recognised text to the clipboard", config.CopyToClipboard);
        AddCheck(_startWithWindows, "Start with Windows", Config.IsStartWithWindowsEnabled());

        BuildCloudControls(config);

        Controls.Add(BuildRoot());
    }

    /// <summary>
    /// Scrolling rows, fixed buttons.
    ///
    /// The buttons deliberately live outside the scrolling region. As the last row of the
    /// grid they work only while the whole dialog fits on screen: once the content grows past
    /// the desktop the form clamps, and Save and Cancel are the rows that fall off the bottom
    /// - unreachable, with no way to scroll to them. Anything that must always be clickable
    /// belongs in the fixed row, not the scrolling one.
    /// </summary>
    private Control BuildRoot()
    {
        // AutoScroll but explicitly NOT AutoSize: an auto-sizing panel reports its full
        // content height as its own size, so it never considers itself overfull and no
        // scrollbar is shown. The panel has to be allowed to be smaller than its contents
        // for scrolling to engage at all.
        _scroll = new Panel
        {
            AutoScroll = true,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };
        _scroll.Controls.Add(_grid);

        var root = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 2,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // rows: absorb the slack
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // buttons: never squeezed

        _buttons = BuildButtons();

        root.Controls.Add(_scroll, 0, 0);
        root.Controls.Add(_buttons, 0, 1);
        return root;
    }

    /// <summary>
    /// Sizes the dialog from its content, then clamps it to the desktop.
    ///
    /// This replaces `Form.AutoSize`, which is content-driven but unbounded: it happily
    /// sized the dialog past the bottom of the screen, taking the Save button with it. The
    /// dimensions here are still entirely derived from the content and therefore from the
    /// system font - nothing is hardcoded in pixels - the desktop only ever acts as a
    /// ceiling.
    ///
    /// Consulting Screen here is not the coordinate-space mistake SPEC 4 warns about: that
    /// rule governs capture and overlay geometry, which must stay in one physical-pixel
    /// space indexed against the freeze frame. This is an ordinary dialog asking how much
    /// desktop it may occupy, in its own DPI context.
    /// </summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        var working = Screen.FromControl(this).WorkingArea;
        var content = _grid.PreferredSize;

        var chrome = new Size(Width - ClientSize.Width, Height - ClientSize.Height);
        var maxClient = new Size(working.Width - chrome.Width, working.Height - chrome.Height);

        var desiredHeight = content.Height + _buttons!.PreferredSize.Height;

        // Reserve the vertical scrollbar unconditionally rather than predicting whether one
        // will appear. The prediction - "only if the height clamps" - compared a pre-layout
        // PreferredSize against the working area, and wrapped labels measure taller once
        // they have actually been laid out. When it came up short the scrollbar appeared
        // anyway, unbudgeted, took its width out of the grid, and produced a horizontal
        // scrollbar. Paying for it always costs a few pixels of width when it turns out to
        // be unnecessary, and cannot be wrong.
        var desiredWidth = content.Width + SystemInformation.VerticalScrollBarWidth;

        ClientSize = new Size(
            Math.Min(desiredWidth, maxClient.Width),
            Math.Min(desiredHeight, maxClient.Height));

        // Re-centre: the size changed after StartPosition had already placed the form.
        Location = new Point(
            working.X + Math.Max(0, (working.Width - Width) / 2),
            working.Y + Math.Max(0, (working.Height - Height) / 2));

        // Only now, once the size above has been taken from the content: pinning the grid
        // any earlier would feed the form's default width back into that measurement.
        _scroll!.ClientSizeChanged += (_, _) => FitToViewportWidth();
        FitToViewportWidth();
    }

    /// <summary>
    /// Pins the grid to the width of the visible area, so the dialog only ever scrolls
    /// vertically.
    /// </summary>
    private void FitToViewportWidth()
    {
        if (_scroll is null) return;

        var width = _scroll.ClientSize.Width;
        if (width <= 0 || _grid.MaximumSize.Width == width) return;

        // Min and Max together: AutoSize honours both, so this fixes the width while
        // leaving the height free to grow with the content.
        _grid.MinimumSize = new Size(width, 0);
        _grid.MaximumSize = new Size(width, 0);

        var textWidth = width - _pad * 2;
        foreach (var (control, natural) in _wrapping)
            control.MaximumSize = new Size(Math.Min(natural, textWidth), 0);
    }

    // --- Layout helpers ---------------------------------------------------------

    private void AddRow(string label, Control control)
    {
        var caption = new Label
        {
            Text = label,
            AutoSize = true,                       // never clip, whatever the font size
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, _pad, _pad, _pad),
        };

        control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        control.Margin = new Padding(0, _pad / 2, 0, _pad / 2);
        if (control.Width < _inputWidth) control.Width = _inputWidth;

        _grid.Controls.Add(caption, 0, _grid.RowCount);
        _grid.Controls.Add(control, 1, _grid.RowCount);
        _grid.RowCount++;
    }

    private void AddHint(string text)
    {
        var hint = new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            // Bounding the width makes AutoSize wrap rather than stretch the dialog.
            MaximumSize = new Size(_inputWidth, 0),
            Margin = new Padding(0, 0, 0, _pad),
        };

        _wrapping.Add((hint, _inputWidth));
        _grid.Controls.Add(hint, 1, _grid.RowCount);
        _grid.RowCount++;
    }

    private void AddCheck(CheckBox box, string text, bool value)
    {
        box.Text = text;
        box.Checked = value;
        box.AutoSize = true;
        box.Margin = new Padding(0, _pad / 2, 0, _pad / 2);

        // Spans both columns, so nothing else bounds it: without a cap a long caption sets
        // the width of the whole dialog, and clips instead of wrapping once that width is
        // no longer available.
        _wrapping.Add((box, int.MaxValue));
        _grid.Controls.Add(box, 0, _grid.RowCount);
        _grid.SetColumnSpan(box, 2);
        _grid.RowCount++;
    }

    /// <summary>
    /// Slider, its numeric readout and the Test button on one line.
    ///
    /// A TableLayoutPanel rather than a FlowLayoutPanel: flow lays items out from the top
    /// edge, and the TrackBar is much taller than the label and button, so the row ends up
    /// visibly ragged. Anchor.None centres each control in its own cell instead.
    /// </summary>
    private Control BuildRateRow()
    {
        var row = new TableLayoutPanel
        {
            ColumnCount = 3,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = Padding.Empty,
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _rate.AutoSize = false;
        _rate.Width = _inputWidth - Font.Height * 6;
        _rate.Height = Font.Height * 2;
        _rate.Anchor = AnchorStyles.None;
        _rate.Margin = new Padding(0, 0, _pad, 0);

        _rateLabel.AutoSize = true;
        _rateLabel.Anchor = AnchorStyles.None;
        _rateLabel.Margin = new Padding(0, 0, _pad, 0);

        var test = new Button
        {
            Text = "Test",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.None,
            Margin = Padding.Empty,
        };
        test.Click += OnTestVoice;

        row.Controls.Add(_rate, 0, 0);
        row.Controls.Add(_rateLabel, 1, 0);
        row.Controls.Add(test, 2, 0);
        return row;
    }

    private Control BuildButtons()
    {
        var ok = new Button
        {
            Text = "Save",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(_pad, 0, 0, 0),
        };
        ok.Click += (_, _) =>
        {
            Result = Compose();
            ApiKey = _apiKey.Text;
        };

        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(_pad, 0, 0, 0),
        };

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,   // Cancel rightmost
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            // Padded on all sides now that this sits against the form edge rather than
            // inside the grid, which supplied its own padding.
            Margin = new Padding(_pad, _pad, _pad, _pad),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        AcceptButton = ok;
        CancelButton = cancel;
        return buttons;
    }

    // --- Population -------------------------------------------------------------

    private void BuildLanguageCombo(Config config)
    {
        _language.DropDownStyle = ComboBoxStyle.DropDownList;
        _language.Items.Add(AutomaticLanguage);

        foreach (var tag in OcrService.AvailableLanguages()) _language.Items.Add(tag);

        _language.SelectedItem = config.OcrLanguage is null ? AutomaticLanguage : config.OcrLanguage;
        if (_language.SelectedIndex < 0) _language.SelectedIndex = 0;
    }

    private void BuildVoiceCombo(Config config)
    {
        _voice.DropDownStyle = ComboBoxStyle.DropDownList;
        _voice.Items.Add(AutomaticVoice);

        // SPEC 7.2: show what is actually installed, never a hardcoded list.
        foreach (var voice in SpeechService.AvailableVoices())
            _voice.Items.Add(new VoiceItem(voice.Id, $"{voice.DisplayName} ({voice.Language})"));

        _voice.SelectedIndex = 0;
        if (config.VoiceId is not null)
        {
            foreach (var item in _voice.Items)
            {
                if (item is VoiceItem v && v.Id == config.VoiceId)
                {
                    _voice.SelectedItem = item;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// The cloud reading engine (SPEC 14). Presented last and behind an explicit opt-in,
    /// because enabling it changes three properties the app otherwise guarantees: readings
    /// stop being free, stop working offline, and stop staying on the machine.
    /// </summary>
    private void BuildCloudControls(Config config)
    {
        AddSeparator();

        AddCheck(_useCloud, "Read with a cloud model instead of local OCR", config.UseCloudEngine);
        AddHint("OCR/voice will be remotely processed by OpenAI which will consume some credits. " +
                "Will fall back to local processing if there's an error.");

        _apiKey.UseSystemPasswordChar = true;
        _apiKey.Text = ApiKeyStore.Load() ?? string.Empty;
        AddRow("OpenAI API key", _apiKey);

        _cloudModel.DropDownStyle = ComboBoxStyle.DropDownList;
        _cloudModel.Items.Add(new ModelItem(
            "gpt-realtime-2.1-mini", "gpt-realtime-2.1-mini (about 2c a reading)"));
        _cloudModel.Items.Add(new ModelItem(
            "gpt-realtime-2.1", "gpt-realtime-2.1 (about 6c a reading)"));
        SelectModel(config.CloudModel);
        AddRow("Model", _cloudModel);

        _cloudVoice.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var voice in CloudVoices) _cloudVoice.Items.Add(voice);
        SelectCloudVoice(config.CloudVoice);
        AddRow("Cloud voice", _cloudVoice);

        AddCheck(_overridePrompt, "Override default reading prompt", config.OverrideCloudPrompt);
        _customPrompt = config.CloudPrompt ?? string.Empty;

        _cloudPrompt.Multiline = true;
        _cloudPrompt.ScrollBars = ScrollBars.Vertical;
        _cloudPrompt.Height = Font.Height * 5;
        AddRow("Reading prompt", _cloudPrompt);
        AddHint("Instructions for the model.");

        _overridePrompt.CheckedChanged += (_, _) =>
        {
            // Capture on the way out, while the box still holds what the user wrote. Doing
            // it inside UpdatePromptSource would also run on the initial call, where the box
            // is still empty, and would wipe the prompt loaded from the config.
            if (!_overridePrompt.Checked) _customPrompt = _cloudPrompt.Text;
            UpdatePromptSource();
        };
        UpdatePromptSource();

        _useCloud.CheckedChanged += (_, _) => UpdateCloudEnabled();
        UpdateCloudEnabled();
    }

    /// <summary>
    /// Swaps the box between the built-in prompt and the user's own.
    ///
    /// The default is shown read-only rather than blanked or hidden: seeing the exact text
    /// the app will send - and being able to select and copy it as a starting point.
    /// </summary>
    private void UpdatePromptSource()
    {
        if (_overridePrompt.Checked)
        {
            _cloudPrompt.ReadOnly = false;
            _cloudPrompt.BackColor = SystemColors.Window;
            // Seed an empty custom prompt from the default
            _cloudPrompt.Text = string.IsNullOrWhiteSpace(_customPrompt)
                ? Config.DefaultCloudPrompt
                : _customPrompt;
        }
        else
        {
            _cloudPrompt.ReadOnly = true;
            _cloudPrompt.BackColor = SystemColors.Control;
            _cloudPrompt.Text = Config.DefaultCloudPrompt;
        }
    }

    /// <summary>
    /// The voices the Realtime API accepts, from its documented list. Unlike the local
    /// voices (SPEC 7.2) there is nothing to enumerate at runtime, so this is a hardcoded
    /// list that has to be updated when OpenAI adds one:
    /// https://developers.openai.com/api/docs/guides/realtime-conversations#voice-options
    /// </summary>
    private static readonly string[] CloudVoices =
    [
        "alloy", "ash", "ballad", "cedar", "coral", "echo", "marin", "sage", "shimmer", "verse",
    ];

    private void SelectCloudVoice(string? voice)
    {
        if (string.IsNullOrWhiteSpace(voice)) voice = Config.DefaultCloudVoice;
        if (!_cloudVoice.Items.Contains(voice)) _cloudVoice.Items.Add(voice);
        _cloudVoice.SelectedItem = voice;
    }

    private void SelectModel(string? id)
    {
        _cloudModel.SelectedIndex = 0;
        foreach (var item in _cloudModel.Items)
        {
            if (item is ModelItem model && model.Id == id)
            {
                _cloudModel.SelectedItem = item;
                return;
            }
        }
    }

    /// <summary>Greys the cloud fields out rather than hiding them, so the dialog does not
    /// resize as the checkbox is toggled.</summary>
    private void UpdateCloudEnabled()
    {
        _apiKey.Enabled = _useCloud.Checked;
        _cloudModel.Enabled = _useCloud.Checked;
        _cloudVoice.Enabled = _useCloud.Checked;
        _overridePrompt.Enabled = _useCloud.Checked;
        _cloudPrompt.Enabled = _useCloud.Checked;
    }

    private void AddSeparator()
    {
        var rule = new Label
        {
            AutoSize = false,
            BorderStyle = BorderStyle.Fixed3D,
            Height = 2,
            Margin = new Padding(0, _pad, 0, _pad),
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
        };

        _grid.Controls.Add(rule, 0, _grid.RowCount);
        _grid.SetColumnSpan(rule, 2);
        _grid.RowCount++;
    }

    private void BuildRateControls(Config config)
    {
        // TrackBar is integral, so the 0.5-6.0 range is held as tenths.
        _rate.Minimum = 5;
        _rate.Maximum = 60;
        _rate.TickFrequency = 5;
        _rate.Value = Math.Clamp((int)Math.Round(config.SpeakingRate * 10), 5, 60);
        _rate.ValueChanged += (_, _) => UpdateRateLabel();
        UpdateRateLabel();
    }

    private void UpdateRateLabel() => _rateLabel.Text = $"{_rate.Value / 10.0:0.0}×";

    private async void OnTestVoice(object? sender, EventArgs e)
    {
        var button = (Button)sender!;
        button.Enabled = false;
        try
        {
            using var speech = new SpeechService();
            speech.ApplySettings(Compose());
            await speech.SpeakAsync("This is how Select and Read will sound.", CancellationToken.None);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Test failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            button.Enabled = true;
        }
    }

    private Config Compose() => new()
    {
        CaptureHotkey = _capture.Value.ToString(),
        PlaybackHotkey = _playback.Value.ToString(),
        OcrLanguage = _language.SelectedItem as string is { } tag && tag != AutomaticLanguage ? tag : null,
        VoiceId = (_voice.SelectedItem as VoiceItem)?.Id,
        SpeakingRate = _rate.Value / 10.0,
        UpscaleBeforeOcr = _upscale.Checked,
        CopyToClipboard = _clipboard.Checked,
        StartWithWindows = _startWithWindows.Checked,
        UseCloudEngine = _useCloud.Checked,
        CloudModel = (_cloudModel.SelectedItem as ModelItem)?.Id ?? Config.DefaultCloudModel,
        CloudVoice = _cloudVoice.SelectedItem as string ?? Config.DefaultCloudVoice,
        OverrideCloudPrompt = _overridePrompt.Checked,
        CloudPrompt = _overridePrompt.Checked ? _cloudPrompt.Text : _customPrompt,
    };

    private const string AutomaticLanguage = "(automatic)";
    private const string AutomaticVoice = "(best available)";

    private sealed record VoiceItem(string Id, string Display)
    {
        public override string ToString() => Display;
    }

    private sealed record ModelItem(string Id, string Display)
    {
        public override string ToString() => Display;
    }

    /// <summary>Read-only text box that captures a modifier+key combination.</summary>
    private sealed class HotkeyBox : TextBox
    {
        internal Hotkey Value { get; private set; }

        internal HotkeyBox(Hotkey initial)
        {
            Value = initial;
            Text = initial.ToString();
            ReadOnly = true;
            Cursor = Cursors.Hand;
        }

        // Ensures arrow/tab/function keys reach OnKeyDown rather than being handled as
        // navigation.
        protected override bool IsInputKey(Keys keyData) => true;

        protected override void OnKeyDown(KeyEventArgs e)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;

            if (e.KeyCode is Keys.ControlKey or Keys.ShiftKey or Keys.Menu
                or Keys.LWin or Keys.RWin or Keys.None)
            {
                return;
            }

            uint modifiers = 0;
            if (e.Control) modifiers |= Native.MOD_CONTROL;
            if (e.Shift) modifiers |= Native.MOD_SHIFT;
            if (e.Alt) modifiers |= Native.MOD_ALT;

            // A modifier-less hotkey would swallow a bare key system-wide.
            if (modifiers == 0) return;

            Value = new Hotkey(modifiers, (uint)e.KeyCode);
            Text = Value.ToString();
        }
    }
}
