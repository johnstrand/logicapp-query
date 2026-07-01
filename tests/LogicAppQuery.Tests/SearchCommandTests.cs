using System;
using Xunit;
using LogicAppQuery;

namespace LogicAppQuery.Tests;

public class SearchCommandTests
{
    [Fact]
    public void BuildSnippet_TermNotFound_ReturnsEmptyString()
    {
        var result = SearchCommand.BuildSnippet("This is some content", "missing");
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void BuildSnippet_MatchInShortText_ReturnsEntireText()
    {
        var result = SearchCommand.BuildSnippet("Short text with match here", "match");
        Assert.Equal("Short text with match here", result);
    }

    [Fact]
    public void BuildSnippet_MatchNearBeginning_ReturnsTextWithSuffix()
    {
        var content = "match is here and then there is a lot of extra padding text to make sure we exceed the one hundred character radius boundary limit so it adds a suffix.";
        var result = SearchCommand.BuildSnippet(content, "match");

        Assert.StartsWith("match", result);
        Assert.EndsWith("...", result);
        Assert.DoesNotContain("...match", result); // No prefix
    }

    [Fact]
    public void BuildSnippet_MatchNearEnd_ReturnsTextWithPrefix()
    {
        var content = "There is a lot of extra padding text to make sure we exceed the one hundred character radius boundary limit so it adds a prefix for the match.";
        var result = SearchCommand.BuildSnippet(content, "match");

        Assert.StartsWith("...", result);
        Assert.EndsWith("match.", result);
    }

    [Fact]
    public void BuildSnippet_MatchInMiddleOfLongText_ReturnsTextWithPrefixAndSuffix()
    {
        var padding = new string('x', 150);
        var content = $"{padding} match {padding}";

        var result = SearchCommand.BuildSnippet(content, "match");

        Assert.StartsWith("...", result);
        Assert.EndsWith("...", result);
        Assert.Contains("match", result);
    }

    [Fact]
    public void BuildSnippet_TextWithLineEndings_ReplacesWithSpaces()
    {
        var content = "line1\r\nline2\nmatch\rline4";
        var result = SearchCommand.BuildSnippet(content, "match");

        // .ReplaceLineEndings(" ") converts \r\n, \n, and \r each to a single space " ".
        // So "line1\r\nline2" becomes "line1 line2". Wait! In .NET, ReplaceLineEndings
        // treats "\r\n" as a single line ending, so it gets replaced with one " ".
        // Let's assert against the actual framework behavior.
        Assert.Equal("line1 line2 match line4", result);
    }

    [Fact]
    public void BuildSnippet_SnippetExceedsMaxLength_TruncatesAndAddsSuffix()
    {
        // 300 is the max length. With prefix (3), match (5), and 100 on each side, we don't naturally hit 300.
        // Wait, SnippetRadius is 100. Match length is 5.
        // max length of raw string is 100 + 5 + 100 = 205.
        // So the raw snippet length would be around 205 + prefix (3) + suffix (3) = 211.
        // MaxSnippetLength is 300, so it normally wouldn't truncate.
        // Let's create a huge search term to force it.
        var searchTerm = new string('Y', 150);
        var padding = new string('x', 150);
        var content = $"{padding} {searchTerm} {padding}";

        var result = SearchCommand.BuildSnippet(content, searchTerm);

        Assert.Equal(303, result.Length); // MaxSnippetLength (300) + "..." (3)
        Assert.EndsWith("...", result);
    }

    [Fact]
    public void BuildSnippet_CaseInsensitiveMatch_ReturnsCorrectSnippet()
    {
        var result = SearchCommand.BuildSnippet("This is some CONTENT here", "content");
        Assert.Equal("This is some CONTENT here", result);
    }
}
