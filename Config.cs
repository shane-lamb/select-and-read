using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace SelectAndRead;

/// <summary>User settings, persisted to %APPDATA%\SelectAndRead\config.json (SPEC 10).</summary>
internal sealed class Config
{
    public string CaptureHotkey { get; set; } = Hotkey.DefaultCapture.ToString();
    public string StopHotkey { get; set; } = Hotkey.DefaultStop.ToString();

    /// <summary>BCP-47 tag, or null for the user profile default.</summary>
    public string? OcrLanguage { get; set; }

    /// <summary>Voice id, or null to auto-select the best installed voice (SPEC 7.2).</summary>
    public string? VoiceId { get; set; }

    /// <summary>Valid range 0.5 - 6.0.</summary>
    public double SpeakingRate { get; set; } = 1.0;

    public bool UpscaleBeforeOcr { get; set; } = true;
    public bool CopyToClipboard { get; set; } = true;
    public bool StartWithWindows { get; set; }

    [JsonIgnore]
    public Hotkey Capture => Hotkey.ParseOrDefault(CaptureHotkey, Hotkey.DefaultCapture);

    [JsonIgnore]
    public Hotkey Stop => Hotkey.ParseOrDefault(StopHotkey, Hotkey.DefaultStop);

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
}
