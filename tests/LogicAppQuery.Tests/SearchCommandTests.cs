using Spectre.Console.Testing;
using System.Collections.Generic;
using System.IO;
using System;
using Xunit;
using LogicAppQuery;

namespace LogicAppQuery.Tests;

public class SearchCommandTests
{
    private class FakeFailingArmClient : IArmClient
    {
        public Task<string> DiscoverResourceGroupAsync(string subscriptionId, string appName, CancellationToken ct)
        {
            throw new Exception("Simulated discovery failure");
        }

        public IAsyncEnumerable<WorkflowRun> ListRunsAsync(string subscriptionId, string resourceGroup, string appName, string workflowName, DateTimeOffset? start, DateTimeOffset? end, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        public IAsyncEnumerable<WorkflowAction> ListActionsAsync(string subscriptionId, string resourceGroup, string appName, string workflowName, string runName, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        public Task<string?> FetchContentAsync(ContentLink link, CancellationToken ct)
        {
            throw new NotImplementedException();
        }
    }

    private class ConcurrencyTrackingArmClient : IArmClient
    {
        private int _currentConcurrency = 0;
        public int MaxConcurrency { get; private set; } = 0;
        private readonly object _lock = new();
        private readonly int _actionCount;

        public ConcurrencyTrackingArmClient(int actionCount)
        {
            _actionCount = actionCount;
        }

        public Task<string> DiscoverResourceGroupAsync(string subscriptionId, string appName, CancellationToken ct)
            => Task.FromResult("rg");

        public IAsyncEnumerable<WorkflowRun> ListRunsAsync(string subscriptionId, string resourceGroup, string appName, string workflowName, DateTimeOffset? start, DateTimeOffset? end, CancellationToken ct = default)
            => throw new NotImplementedException();

        public async IAsyncEnumerable<WorkflowAction> ListActionsAsync(string subscriptionId, string resourceGroup, string appName, string workflowName, string runName, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            for (int i = 0; i < _actionCount; i++)
            {
                var action = new WorkflowAction($"Action{i}", new WorkflowActionProperties(
                    Status: "Succeeded",
                    InputsLink: new ContentLink($"http://example.com/input{i}", 100),
                    OutputsLink: new ContentLink($"http://example.com/output{i}", 100),
                    Inputs: null,
                    Outputs: null
                ));
                yield return action;
                await Task.Yield();
            }
        }

        public async Task<string?> FetchContentAsync(ContentLink link, CancellationToken ct)
        {
            lock (_lock)
            {
                _currentConcurrency++;
                if (_currentConcurrency > MaxConcurrency)
                {
                    MaxConcurrency = _currentConcurrency;
                }
            }

            try
            {
                await Task.Delay(50, ct); // Simulate network latency to allow concurrency to build up
                return "mock_content";
            }
            finally
            {
                lock (_lock)
                {
                    _currentConcurrency--;
                }
            }
        }
    }

    [Fact]
    public async Task BuildRunContentAsync_LimitsConcurrency()
    {
        // Arrange
        var fakeClient = new ConcurrencyTrackingArmClient(actionCount: 20); // 20 actions = 40 requests + 1 trigger request = 41 requests
        var command = new SearchCommand(fakeClient);

        var run = new WorkflowRun("run1", new WorkflowRunProperties(
            Status: "Succeeded",
            StartTime: DateTimeOffset.UtcNow,
            Trigger: new WorkflowRunTrigger(new ContentLink("http://example.com/trigger", 100), null)
        ));

        // Act
        var result = await command.BuildRunContentAsync(
            run, "subId", "rg", "appName", "workflowName", CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.True(fakeClient.MaxConcurrency <= 10, $"Max concurrency was {fakeClient.MaxConcurrency}, expected <= 10");
        Assert.True(fakeClient.MaxConcurrency > 1, $"Max concurrency was {fakeClient.MaxConcurrency}, expected > 1 to ensure it actually ran concurrently");
    }

    [Fact]
    public async Task ExecuteAsync_ResourceGroupDiscoveryFails_ReturnsGracefully()
    {
        // Arrange
        var fakeClient = new FakeFailingArmClient();
        var command = new SearchCommand(fakeClient);

        // Act & Assert
        // We expect it to write the error to console and return without throwing
        var ex = await Record.ExceptionAsync(() => command.ExecuteAsync(
            "subId", "appName", "workflowName", "search", null, null, CancellationToken.None));

        Assert.Null(ex); // Ensures it returns gracefully and doesn't crash
    }

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

    [Fact]
    public void BuildSnippet_EmptySearchTerm_ReturnsEntireContent()
    {
        var result = SearchCommand.BuildSnippet("content", "");
        Assert.Equal("content", result);
    }

    [Fact]
    public void BuildSnippet_EmptyContent_ReturnsEmptyString()
    {
        var result = SearchCommand.BuildSnippet("", "searchTerm");
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void BuildSnippet_EmptyContentAndEmptySearchTerm_ReturnsEmptyString()
    {
        var result = SearchCommand.BuildSnippet("", "");
        Assert.Equal(string.Empty, result);
    }

    private class FakeArmClient : IArmClient
    {
        public Task<string> DiscoverResourceGroupAsync(string subscriptionId, string appName, CancellationToken ct)
            => Task.FromResult("rg");

        public async IAsyncEnumerable<WorkflowRun> ListRunsAsync(string subscriptionId, string resourceGroup, string appName, string workflowName, DateTimeOffset? start, DateTimeOffset? end, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new WorkflowRun("run1", new WorkflowRunProperties("Succeeded", DateTimeOffset.UtcNow, null));
            yield return new WorkflowRun("run2", new WorkflowRunProperties("Failed", DateTimeOffset.UtcNow, null));
            await Task.CompletedTask;
        }

        public async IAsyncEnumerable<WorkflowAction> ListActionsAsync(string subscriptionId, string resourceGroup, string appName, string workflowName, string runName, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            if (runName == "run1")
            {
                yield return new WorkflowAction("action1", new WorkflowActionProperties("Succeeded", new ContentLink("http://test/input", 10), null, null, null));
            }
            await Task.CompletedTask;
        }

        public Task<string?> FetchContentAsync(ContentLink link, CancellationToken ct)
        {
            if (link.Uri == "http://test/input") return Task.FromResult<string?>("This is my secretsearchTerm here.");
            return Task.FromResult<string?>(null);
        }
    }

    [Fact]
    public async Task ExecuteAsync_FindsMatchesAndWritesToConsole()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var fakeClient = new FakeArmClient();
            var testConsole = new TestConsole();
            testConsole.Profile.Capabilities.Interactive = false;
            var command = new SearchCommand(fakeClient, cacheDirectory: tempDir, ansiConsole: testConsole);

            await command.ExecuteAsync("subId", "appName", "workflowName", "secretsearchTerm", null, null, CancellationToken.None);

            var output = testConsole.Output;
            Assert.Contains("match(es)", output);
            Assert.Contains("Searched 2 run(s)", output);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}
