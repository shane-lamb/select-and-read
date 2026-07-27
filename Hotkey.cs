using System.Text;

namespace SelectAndRead;

/// <summary>
/// A parsed global hotkey: Win32 modifier flags plus a virtual key code.
/// Round-trips to the human-readable form stored in config.json ("Ctrl+Shift+F9").
/// </summary>
internal sealed record Hotkey(uint Modifiers, uint VirtualKey)
{
    /// <summary>SPEC 8.2 defaults. Function keys avoid the AltGr and in-app shortcut traps.</summary>
    internal static readonly Hotkey DefaultCapture =
        new(Native.MOD_CONTROL | Native.MOD_SHIFT, (uint)Keys.F9);

    internal static readonly Hotkey DefaultStop =
        new(Native.MOD_CONTROL | Native.MOD_SHIFT, (uint)Keys.F10);

    internal static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = DefaultCapture;
        if (string.IsNullOrWhiteSpace(text)) return false;

        uint mods = 0;
        uint vk = 0;

        foreach (var rawPart in text.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = rawPart.Trim();
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= Native.MOD_CONTROL; break;
                case "shift": mods |= Native.MOD_SHIFT; break;
                case "alt": mods |= Native.MOD_ALT; break;
                case "win" or "windows": mods |= Native.MOD_WIN; break;
                default:
                    if (!Enum.TryParse<Keys>(part, ignoreCase: true, out var key)) return false;
                    // Reject a second non-modifier key rather than silently taking the last.
                    if (vk != 0) return false;
                    vk = (uint)key;
                    break;
            }
        }

        // A hotkey with no modifier would swallow a bare key system-wide.
        if (vk == 0 || mods == 0) return false;

        hotkey = new Hotkey(mods, vk);
        return true;
    }

    internal static Hotkey ParseOrDefault(string? text, Hotkey fallback) =>
        TryParse(text, out var hk) ? hk : fallback;

    public override string ToString()
    {
        var sb = new StringBuilder();
        if ((Modifiers & Native.MOD_CONTROL) != 0) sb.Append("Ctrl+");
        if ((Modifiers & Native.MOD_SHIFT) != 0) sb.Append("Shift+");
        if ((Modifiers & Native.MOD_ALT) != 0) sb.Append("Alt+");
        if ((Modifiers & Native.MOD_WIN) != 0) sb.Append("Win+");
        sb.Append((Keys)VirtualKey);
        return sb.ToString();
    }
}
