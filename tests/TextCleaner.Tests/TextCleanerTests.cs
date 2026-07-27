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
