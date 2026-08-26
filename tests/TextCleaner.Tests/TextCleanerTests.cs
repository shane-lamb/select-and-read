using System.Drawing;

namespace SelectAndRead.Tests;

/// <summary>
/// Covers the text normalisation rules in SPEC 6. These run on any platform, which makes
/// them the only part of the app verifiable during development on macOS.
/// </summary>
public class TextCleanerTests
{
    [Fact]
    public void JoinsLinesWithSingleSpaces()
    {
        var result = TextCleaner.Clean(["The quick brown fox", "jumps over the lazy dog."]);
        Assert.Equal("The quick brown fox jumps over the lazy dog.", result);
    }

    [Fact]
    public void CollapsesInternalWhitespace()
    {
        var result = TextCleaner.Clean(["Wide    gaps\tand\ttabs", "  padded  line  "]);
        Assert.Equal("Wide gaps and tabs padded line", result);
    }

    [Fact]
    public void RejoinsHyphenatedWordAcrossLines()
    {
        var result = TextCleaner.Clean(["particularly inter-", "esting results"]);
        Assert.Equal("particularly interesting results", result);
    }

    [Fact]
    public void KeepsHyphenWhenNextLineStartsUppercase()
    {
        // A trailing hyphen before a capital is far more likely a dash or a list marker
        // than a broken word, so joining would corrupt the text.
        var result = TextCleaner.Clean(["see appendix -", "Notes follow"]);
        Assert.Equal("see appendix - Notes follow", result);
    }

    [Fact]
    public void DropsSeparatorRulesAndTableBorders()
    {
        var result = TextCleaner.Clean(["Heading", "--------", "+---+---+", "Body text"]);
        Assert.Equal("Heading Body text", result);
    }

    [Fact]
    public void KeepsLinesThatContainAnyAlphanumeric()
    {
        // "| 42 |" is a table row with real content and must survive.
        var result = TextCleaner.Clean(["| 42 |"]);
        Assert.Equal("| 42 |", result);
    }

    [Fact]
    public void SkipsBlankAndWhitespaceOnlyLines()
    {
        var result = TextCleaner.Clean(["First", "", "   ", "Second"]);
        Assert.Equal("First Second", result);
    }

    [Fact]
    public void ReturnsEmptyForNoRecognisedText()
    {
        // Drives the "No text found." path in SPEC 2.6.
        Assert.Equal(string.Empty, TextCleaner.Clean([]));
        Assert.Equal(string.Empty, TextCleaner.Clean(["", "***", "---"]));
    }

    [Fact]
    public void ToleratesNullLines()
    {
        var result = TextCleaner.Clean([null!, "Real text", null!]);
        Assert.Equal("Real text", result);
    }

    [Fact]
    public void JoinsHyphenAcrossADroppedSeparatorLine()
    {
        // The separator is dropped first, so the hyphen rule sees "inter-" next to
        // "esting" and still joins them. Documents the interaction between the two rules.
        var result = TextCleaner.Clean(["inter-", "-----", "esting"]);
        Assert.Equal("interesting", result);
    }
}

/// <summary>
/// Covers the span table that ties cleaned text back to the screen (SPEC 16.2). Every case
/// here is a way cleaning moves text relative to its source: the offsets are what a speech
/// engine reports, so an error of one character points the highlight at the wrong word.
/// </summary>
public class TextCleanerSpanTests
{
    private static Rectangle Box(int x) => new(x, 100, 40, 20);

    private static IReadOnlyList<TextCleaner.Word> Line(params string[] words) =>
        words.Select((w, i) => new TextCleaner.Word(w, Box(i * 50))).ToList();

    /// <summary>The text each span actually points at, which is the property that matters.</summary>
    private static string[] Sliced(TextCleaner.Result result) =>
        result.Spans.Select(s => result.Text.Substring(s.Start, s.Length)).ToArray();

    [Fact]
    public void SpansAddressTheirOwnWords()
    {
        var result = TextCleaner.CleanWords([Line("The", "quick"), Line("brown", "fox")]);

        Assert.Equal("The quick brown fox", result.Text);
        Assert.Equal(["The", "quick", "brown", "fox"], Sliced(result));
    }

    [Fact]
    public void SpansCarryTheirSourceBox()
    {
        var result = TextCleaner.CleanWords([Line("alpha", "beta")]);

        Assert.Equal(Box(0), result.Spans[0].Box);
        Assert.Equal(Box(50), result.Spans[1].Box);
    }

    [Fact]
    public void DroppedLinesDoNotShiftLaterSpans()
    {
        // The separator contributes no text and no span, so everything after it has to be
        // numbered as though it were never there.
        var result = TextCleaner.CleanWords([Line("First"), Line("-----"), Line("Second")]);

        Assert.Equal("First Second", result.Text);
        Assert.Equal(["First", "Second"], Sliced(result));
    }

    [Fact]
    public void DeHyphenationShortensThePrecedingSpan()
    {
        // "inter-" loses its hyphen to the join, so its span must shrink with it - otherwise
        // it would run one character into "esting" and every later offset would be one out.
        var result = TextCleaner.CleanWords([Line("particularly", "inter-"), Line("esting", "results")]);

        Assert.Equal("particularly interesting results", result.Text);
        Assert.Equal(["particularly", "inter", "esting", "results"], Sliced(result));
    }

    [Fact]
    public void CollapsedWhitespaceDoesNotShiftSpans()
    {
        var result = TextCleaner.CleanWords([Line("  padded  ", "\tword\t")]);

        Assert.Equal("padded word", result.Text);
        Assert.Equal(["padded", "word"], Sliced(result));
    }

    [Fact]
    public void EmptyWordsProduceNoSpans()
    {
        var result = TextCleaner.CleanWords([Line("real", "", "   ", "text")]);

        Assert.Equal("real text", result.Text);
        Assert.Equal(["real", "text"], Sliced(result));
    }

    [Fact]
    public void BoxOverFindsTheWordAtASingleCharacterRange()
    {
        var result = TextCleaner.CleanWords([Line("The", "quick", "brown")]);

        // "The" spans 0-2, "quick" 4-8, "brown" 10-14.
        Assert.Equal(Box(0), TextCleaner.BoxOver(result.Spans, 0, 2));
        Assert.Equal(Box(50), TextCleaner.BoxOver(result.Spans, 4, 8));
        Assert.Equal(Box(100), TextCleaner.BoxOver(result.Spans, 10, 14));
    }

    [Fact]
    public void BoxOverEnclosesEveryWordARangeCovers()
    {
        // The case that stranded the mark: a voice says "in 2018" as one unit and reports it
        // as one range spanning both words, so marking only the word the range starts at
        // leaves the mark on "in" for as long as the number takes to say.
        var result = TextCleaner.CleanWords([Line("launched", "in", "2018")]);

        // "launched" 0-7, "in" 9-10, "2018" 12-15.
        var box = TextCleaner.BoxOver(result.Spans, 9, 15);

        Assert.Equal(Rectangle.Union(Box(50), Box(100)), box);
        Assert.NotEqual(Box(50), box);
    }

    [Fact]
    public void BoxOverIncludesAWordTheRangeOnlyPartlyCovers()
    {
        var result = TextCleaner.CleanWords([Line("The", "quick", "brown")]);

        // Starts inside "The" and ends inside "quick"; both are being spoken.
        Assert.Equal(Rectangle.Union(Box(0), Box(50)), TextCleaner.BoxOver(result.Spans, 1, 5));
    }

    [Fact]
    public void BoxOverTreatsASeparatingSpaceAsTheWordAfterIt()
    {
        var result = TextCleaner.CleanWords([Line("The", "quick")]);

        // A range beginning on the space before a word still belongs to that word.
        Assert.Equal(Box(50), TextCleaner.BoxOver(result.Spans, 3, 8));
    }

    [Fact]
    public void BoxOverReturnsNullBeyondSpans()
    {
        var result = TextCleaner.CleanWords([Line("The", "quick")]);

        Assert.Null(TextCleaner.BoxOver(result.Spans, 99, 120));   // past the end
        Assert.Null(TextCleaner.BoxOver(result.Spans, 5, 4));      // inverted
        Assert.Null(TextCleaner.BoxOver([], 0, 3));
    }
}
