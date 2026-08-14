using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;

namespace SelectAndRead;

/// <summary>
/// Turns raw OCR lines into something that sounds right when spoken (SPEC 6).
/// Deliberately pure and free of any Windows API surface, so the fiddliest logic in the
/// app is testable anywhere. <see cref="Rectangle"/> is part of the base framework rather
/// than of System.Drawing.Common, so carrying geometry through here does not compromise
/// that.
/// </summary>
internal static partial class TextCleaner
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>One recognised word and where it sits in the crop.</summary>
    internal readonly record struct Word(string Text, Rectangle Box);

    /// <summary>
    /// Where a word ended up in the cleaned text, and the box it came from.
    ///
    /// This exists because cleaning is not a transformation offsets survive: lines are
    /// dropped, whitespace is collapsed and hyphenated words are fused, so nothing about the
    /// output string says which part of the screen a given character came from. Recording it
    /// during the rewrite is the only way to keep the link (SPEC 16).
    /// </summary>
    internal readonly record struct Span(int Start, int Length, Rectangle Box);

    internal sealed record Result(string Text, IReadOnlyList<Span> Spans);

    /// <summary>
    /// Cleans lines of plain text. Equivalent to treating each line as a single word with no
    /// geometry, which is exactly what it does - one implementation rather than two that
    /// could drift apart and make the spoken text depend on whether anything asked for
    /// spans.
    /// </summary>
    internal static string Clean(IEnumerable<string> lines) =>
        CleanWords(lines.Select(line => (IReadOnlyList<Word>)[new Word(line, Rectangle.Empty)])).Text;

    internal static Result CleanWords(IEnumerable<IReadOnlyList<Word>> lines)
    {
        var kept = new List<List<Word>>();

        foreach (var raw in lines)
        {
            var words = new List<Word>();

            foreach (var word in raw ?? [])
            {
                var text = Whitespace().Replace(word.Text ?? string.Empty, " ").Trim();
                if (text.Length == 0) continue;

                words.Add(word with { Text = text });
            }

            if (words.Count == 0) continue;

            // Drop separator rules and table borders: read aloud they are pure noise.
            if (!words.Any(word => word.Text.Any(char.IsLetterOrDigit))) continue;

            kept.Add(words);
        }

        var sb = new StringBuilder();
        var spans = new List<Span>();

        foreach (var words in kept)
        {
            if (sb.Length > 0)
            {
                // De-hyphenate a word broken across lines, so "inter-" + "esting" is spoken
                // as one word rather than two.
                if (sb[^1] == '-' && char.IsLower(words[0].Text[0]))
                {
                    sb.Length -= 1;

                    // The hyphen was part of the preceding word, so its span has to lose the
                    // character too or every following offset would be one out.
                    var previous = spans[^1];
                    spans[^1] = previous with { Length = previous.Length - 1 };
                }
                else
                {
                    sb.Append(' ');
                }
            }

            for (var i = 0; i < words.Count; i++)
            {
                if (i > 0) sb.Append(' ');

                spans.Add(new Span(sb.Length, words[i].Text.Length, words[i].Box));
                sb.Append(words[i].Text);
            }
        }

        return new Result(sb.ToString(), spans);
    }

    /// <summary>
    /// The box containing the given character offset, or null when the offset falls between
    /// spans or outside the text.
    ///
    /// A speech engine reports position as an offset into the text it was given, so this is
    /// the lookup that turns "which word is being spoken" into "where to draw". Spans are
    /// produced in reading order and never overlap, so a binary search is well defined; the
    /// linear alternative is called once per spoken word, which is often enough for the
    /// difference to be worth the few lines.
    /// </summary>
    internal static Rectangle? BoxAt(IReadOnlyList<Span> spans, int offset)
    {
        var low = 0;
        var high = spans.Count - 1;

        while (low <= high)
        {
            var mid = (low + high) / 2;
            var span = spans[mid];

            if (offset < span.Start) high = mid - 1;
            else if (offset >= span.Start + span.Length) low = mid + 1;
            else return span.Box;
        }

        return null;
    }
}
