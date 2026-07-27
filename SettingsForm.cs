namespace SelectAndRead;

/// <summary>Settings dialog (SPEC 10). Built in code; no designer file.</summary>
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

    /// <summary>Populated when the dialog returns OK.</summary>
    internal Config Result { get; private set; }

    internal SettingsForm(Config config)
    {
        Result = config;

        Text = "Select and Read — Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(430, 396);

        _capture = new HotkeyBox(config.Capture);
        _stop = new HotkeyBox(config.Stop);

        var y = 16;

        AddRow("Capture hotkey", _capture, ref y);
        AddRow("Stop hotkey", _stop, ref y);

        AddHint("Hold Ctrl, Shift or Alt and press a key. Function keys conflict least.", ref y);

        BuildLanguageCombo(config);
        AddRow("OCR language", _language, ref y);

        BuildVoiceCombo(config);
        AddRow("Voice", _voice, ref y);

        BuildRateControls(config);
        AddRow("Speaking rate", _rate, ref y, extraHeight: 12);

        _rateLabel.SetBounds(300, _rate.Top + 4, 44, 20);
        Controls.Add(_rateLabel);

        var test = new Button { Text = "Test", Bounds = new Rectangle(348, _rate.Top, 60, 24) };
        test.Click += OnTestVoice;
        Controls.Add(test);

        y += 8;
        AddCheck(_upscale, "Upscale small text before OCR (improves accuracy)", config.UpscaleBeforeOcr, ref y);
        AddCheck(_clipboard, "Copy recognised text to the clipboard", config.CopyToClipboard, ref y);
        AddCheck(_startWithWindows, "Start with Windows", Config.IsStartWithWindowsEnabled(), ref y);

        var ok = new Button
        {
            Text = "Save",
            DialogResult = DialogResult.OK,
            Bounds = new Rectangle(228, ClientSize.Height - 40, 88, 26),
        };
        ok.Click += (_, _) => Result = Compose();

        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Bounds = new Rectangle(324, ClientSize.Height - 40, 88, 26),
        };

        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    // --- Layout helpers ---------------------------------------------------------

    private void AddRow(string label, Control control, ref int y, int extraHeight = 0)
    {
        Controls.Add(new Label { Text = label, Bounds = new Rectangle(16, y + 3, 120, 20) });
        control.SetBounds(144, y, 264, 24 + extraHeight);
        Controls.Add(control);
        y += 34 + extraHeight;
    }

    private void AddHint(string text, ref int y)
    {
        Controls.Add(new Label
        {
            Text = text,
            Bounds = new Rectangle(144, y - 4, 264, 30),
            ForeColor = SystemColors.GrayText,
        });
        y += 30;
    }

    private void AddCheck(CheckBox box, string text, bool value, ref int y)
    {
        box.Text = text;
        box.Checked = value;
        box.SetBounds(16, y, 392, 22);
        Controls.Add(box);
        y += 26;
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
    };

    private const string AutomaticLanguage = "(automatic)";
    private const string AutomaticVoice = "(best available)";

    private sealed record VoiceItem(string Id, string Display)
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
