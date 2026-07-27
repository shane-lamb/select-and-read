using System.Text;
using System.Text.RegularExpressions;

namespace SelectAndRead;

/// <summary>
/// Turns raw OCR lines into something that sounds right when spoken (SPEC 6).
/// Deliberately pure and free of any Windows API surface, so the fiddliest logic in the
/// app is testable anywhere.
/// </summary>
internal static partial class TextCleaner
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    internal static string Clean(IEnumerable<string> lines)
    {
        var kept = new List<string>();

        foreach (var raw in lines)
        {
            var line = Whitespace().Replace(raw ?? string.Empty, " ").Trim();
            if (line.Length == 0) continue;

            // Drop separator rules and table borders: read aloud they are pure noise.
            if (!line.Any(char.IsLetterOrDigit)) continue;

            kept.Add(line);
        }

        var sb = new StringBuilder();
        foreach (var line in kept)
        {
            if (sb.Length == 0)
            {
                sb.Append(line);
                continue;
            }

            // De-hyphenate a word broken across lines, so "inter-" + "esting" is spoken
            // as one word rather than two.
            if (sb[^1] == '-' && char.IsLower(line[0]))
            {
                sb.Length -= 1;
                sb.Append(line);
            }
            else
            {
                sb.Append(' ').Append(line);
            }
        }

        return sb.ToString().Trim();
    }
}
