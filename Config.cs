using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace SelectAndRead;

/// <summary>User settings, persisted to %APPDATA%\SelectAndRead\config.json (SPEC 10).</summary>
internal sealed class Config
{
    public string CaptureHotkey { get; set; } = Hotkey.DefaultCapture.ToString();

    /// <summary>Pause, resume and replay (SPEC 2.5).</summary>
    public string PlaybackHotkey { get; set; } = Hotkey.DefaultPlayback.ToString();

    /// <summary>BCP-47 tag, or null for the user profile default.</summary>
    public string? OcrLanguage { get; set; }

    /// <summary>Voice id, or null to auto-select the best installed voice (SPEC 7.2).</summary>
    public string? VoiceId { get; set; }

    /// <summary>Valid range 0.5 - 6.0.</summary>
    public double SpeakingRate { get; set; } = 1.0;

    public bool UpscaleBeforeOcr { get; set; } = true;
    public bool CopyToClipboard { get; set; } = true;
    public bool StartWithWindows { get; set; }

    /// <summary>
    /// Whether to mark each word on screen as it is read (SPEC 16). On by default: following
    /// along is the point of it, and a user who does not want a mark is better served by an
    /// obvious switch than by the feature never existing. Has no effect while the cloud
    /// engine is reading, which cannot locate its own words.
    /// </summary>
    public bool HighlightWhileReading { get; set; } = true;

    // --- Cloud reading engine (SPEC 14) -----------------------------------------
    // Opt-in and off by default: the local path is free, offline and private, and none of
    // that should change without the user asking. The API key itself lives in ApiKeyStore,
    // not here.

    public bool UseCloudEngine { get; set; }

    public string CloudModel { get; set; } = DefaultCloudModel;

    public string CloudVoice { get; set; } = DefaultCloudVoice;

    public bool OverrideCloudPrompt { get; set; }

    /// <summary>The user's own prompt. Only in effect when <see cref="OverrideCloudPrompt"/>
    /// is set; otherwise retained purely so toggling the override back on restores it.</summary>
    public string CloudPrompt { get; set; } = string.Empty;

    /// <summary>What the engine should actually send.</summary>
    [JsonIgnore]
    public string EffectiveCloudPrompt =>
        OverrideCloudPrompt && !string.IsNullOrWhiteSpace(CloudPrompt)
            ? CloudPrompt
            : DefaultCloudPrompt;

    internal const string DefaultCloudModel = "gpt-realtime-2.1-mini";
    internal const string DefaultCloudVoice = "cedar";

    /// <summary>
    /// Steers the model towards transcription rather than description. Without an explicit
    /// instruction a realtime model will happily narrate ("This looks like a settings
    /// window showing...") instead of reading.
    /// </summary>
    internal const string DefaultCloudPrompt =
        "Your task is to read ALL the text within the image aloud, verbatim and in natural reading order. " +
        "Speak clearly.";

    [JsonIgnore]
    public Hotkey Capture => Hotkey.ParseOrDefault(CaptureHotkey, Hotkey.DefaultCapture);

    [JsonIgnore]
    public Hotkey Playback => Hotkey.ParseOrDefault(PlaybackHotkey, Hotkey.DefaultPlayback);

    // --- Persistence ------------------------------------------------------------

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    internal static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SelectAndRead");

    internal static string FilePath => Path.Combine(Directory, "config.json");

    /// <summary>Never throws: a missing or corrupt file falls back to defaults (SPEC 10).</summary>
    internal static Config Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new Config();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<Config>(json, JsonOptions) ?? new Config();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Config();
        }
    }

    /// <summary>
    /// Atomic write: serialise to a temp file in the same directory, then replace. A crash
    /// mid-write can then never leave a truncated config behind.
    /// </summary>
    internal void Save()
    {
        System.IO.Directory.CreateDirectory(Directory);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));

        if (File.Exists(FilePath))
            File.Replace(temp, FilePath, destinationBackupFileName: null);
        else
            File.Move(temp, FilePath);
    }

    // --- Start with Windows -----------------------------------------------------
    // HKCU rather than HKLM: no elevation, and per-user is correct for a tray utility.

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "SelectAndRead";

    internal static void ApplyStartWithWindows(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null) return;

        if (enabled)
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe)) key.SetValue(RunValueName, $"\"{exe}\"");
        }
        else if (key.GetValue(RunValueName) is not null)
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
    }

    internal static bool IsStartWithWindowsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValueName) is not null;
    }

    /// <summary>
    /// Points an existing Run entry back at the running exe if it has moved.
    ///
    /// ApplyStartWithWindows records an absolute path, so installing over a copy the user
    /// had been running from their downloads folder strands the entry on a path that no
    /// longer exists - and it otherwise only corrects itself the next time they open
    /// Settings and press Save, which they have no reason to do.
    ///
    /// A missing entry is left missing. Absence is the user's decision not to autostart,
    /// and this must never be the thing that turns it on.
    /// </summary>
    internal static void RepairStartWithWindowsPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(RunValueName) is not string existing) return;

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;

        var current = $"\"{exe}\"";
        if (!string.Equals(existing, current, StringComparison.OrdinalIgnoreCase))
            key.SetValue(RunValueName, current);
    }
}
