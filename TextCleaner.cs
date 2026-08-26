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
    /// The box enclosing every word the given character range touches, or null when the range
    /// touches none. Both ends are inclusive.
    ///
    /// A speech engine reports position as a range of the text it was given, so this is the
    /// lookup that turns "what is being spoken" into "where to draw". It takes a range rather
    /// than a point because a single cue can cover more than one written word - a voice says
    /// "in 2018" as one unit - and marking only the word the range starts at would leave the
    /// mark on "in" for as long as the number takes to say (SPEC 16.1).
    ///
    /// Spans are produced in reading order and never overlap, so the first one is found by
    /// binary search; the walk forward from it is over the handful the range covers.
    /// </summary>
    internal static Rectangle? BoxOver(IReadOnlyList<Span> spans, int from, int to)
    {
        if (to < from) return null;

        var low = 0;
        var high = spans.Count - 1;
        var first = -1;

        // The leftmost span that ends at or after the range starts.
        while (low <= high)
        {
            var mid = (low + high) / 2;
            var span = spans[mid];

            if (span.Start + span.Length <= from)
            {
                low = mid + 1;
            }
            else
            {
                first = mid;
                high = mid - 1;
            }
        }

        if (first < 0) return null;

        Rectangle? box = null;

        for (var i = first; i < spans.Count && spans[i].Start <= to; i++)
        {
            // A range that begins inside the whitespace before a word still belongs to that
            // word, so overlap rather than containment is the test.
            box = box is { } accumulated
                ? Rectangle.Union(accumulated, spans[i].Box)
                : spans[i].Box;
        }

        return box;
    }
}
