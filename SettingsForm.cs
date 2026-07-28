namespace SelectAndRead;

/// <summary>
/// Settings dialog (SPEC 10). Built in code; no designer file.
///
/// Every dimension here is derived from the current font rather than hardcoded in pixels,
/// and the form auto-sizes to its content. An earlier revision positioned controls with
/// absolute SetBounds calls: it looked fine at the default font and became unusable the
/// moment the user raised the system text size - labels clipped to a single character,
/// rows overlapping, and the buttons cut off below the client area.
///
/// Note this is the opposite choice to SelectionOverlay, which pins AutoScaleMode to None
/// on purpose. That form must stay in raw physical pixels because its coordinates index
/// the freeze frame (SPEC 4.1); an ordinary dialog has no such constraint and should scale
/// with the user's font like any other Windows dialog.
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly HotkeyBox _capture;
    private readonly HotkeyBox _stop;
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
    private readonly TextBox _cloudVoice = new();
    private readonly TextBox _cloudPrompt = new();

    private readonly TableLayoutPanel _grid = new();
    private readonly int _inputWidth;
    private readonly int _pad;

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

        // Scale with the system font, and let the content decide the size rather than
        // asserting one.
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        // Font-relative metrics: these track the user's text size instead of fighting it.
        _inputWidth = Font.Height * 14;
        _pad = Font.Height / 2;

        _capture = new HotkeyBox(config.Capture);
        _stop = new HotkeyBox(config.Stop);

        _grid.ColumnCount = 2;
        _grid.AutoSize = true;
        _grid.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _grid.Dock = DockStyle.Fill;
        _grid.Padding = new Padding(_pad);
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        AddRow("Capture hotkey", _capture);
        AddRow("Stop hotkey", _stop);
        AddHint("Hold Ctrl, Shift or Alt and press a key. Function keys conflict least.");

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

        AddButtons();
        Controls.Add(_grid);
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

        _grid.Controls.Add(hint, 1, _grid.RowCount);
        _grid.RowCount++;
    }

    private void AddCheck(CheckBox box, string text, bool value)
    {
        box.Text = text;
        box.Checked = value;
        box.AutoSize = true;
        box.Margin = new Padding(0, _pad / 2, 0, _pad / 2);

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

    private void AddButtons()
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
            Margin = new Padding(0, _pad, 0, 0),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        _grid.Controls.Add(buttons, 0, _grid.RowCount);
        _grid.SetColumnSpan(buttons, 2);
        _grid.RowCount++;

        AcceptButton = ok;
        CancelButton = cancel;
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
        AddHint("Sends the selected area to OpenAI and plays the speech it streams back. " +
                "More accurate on small text, tables and columns, and starts speaking sooner. " +
                "Costs roughly 2 cents per reading and needs an internet connection. " +
                "Falls back to local reading if the call fails.");

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

        _cloudVoice.Text = string.IsNullOrWhiteSpace(config.CloudVoice)
            ? Config.DefaultCloudVoice
            : config.CloudVoice;
        AddRow("Cloud voice", _cloudVoice);

        // Multi-line and tall enough to show the default prompt without scrolling. Height
        // is font-relative like every other metric here, so it grows with the system text
        // size rather than clipping to one line.
        _cloudPrompt.Multiline = true;
        _cloudPrompt.ScrollBars = ScrollBars.Vertical;
        _cloudPrompt.Height = Font.Height * 5;
        _cloudPrompt.Text = string.IsNullOrWhiteSpace(config.CloudPrompt)
            ? Config.DefaultCloudPrompt
            : config.CloudPrompt;
        AddRow("Reading prompt", _cloudPrompt);
        AddHint("What to tell the model. The default asks it to read verbatim; change it to " +
                "summarise, translate, or describe images instead.");

        _useCloud.CheckedChanged += (_, _) => UpdateCloudEnabled();
        UpdateCloudEnabled();
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
        StopHotkey = _stop.Value.ToString(),
        OcrLanguage = _language.SelectedItem as string is { } tag && tag != AutomaticLanguage ? tag : null,
        VoiceId = (_voice.SelectedItem as VoiceItem)?.Id,
        SpeakingRate = _rate.Value / 10.0,
        UpscaleBeforeOcr = _upscale.Checked,
        CopyToClipboard = _clipboard.Checked,
        StartWithWindows = _startWithWindows.Checked,
        UseCloudEngine = _useCloud.Checked,
        CloudModel = (_cloudModel.SelectedItem as ModelItem)?.Id ?? Config.DefaultCloudModel,
        CloudVoice = _cloudVoice.Text,
        CloudPrompt = _cloudPrompt.Text,
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
