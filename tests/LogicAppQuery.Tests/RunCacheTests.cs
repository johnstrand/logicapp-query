using System;
using System.IO;
using System.Threading.Tasks;
using LogicAppQuery;
using Xunit;

namespace LogicAppQuery.Tests;

public class RunCacheTests
{
    [Fact]
    public void Sanitize_ValidName_ReturnsSameName()
    {
        var input = "validName123";
        var result = RunCache.Sanitize(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void Sanitize_NullInput_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => RunCache.Sanitize(null!));
    }

    [Fact]
    public void Sanitize_WithInvalidCharacters_ReplacesWithUnderscores()
    {
        var invalidChars = new string(Path.GetInvalidFileNameChars());
        var input = $"valid{invalidChars[0]}name";
        var result = RunCache.Sanitize(input);
        Assert.Equal("valid_name", result);
    }

    [Theory]
    [InlineData("../foo", "___foo")]
    [InlineData("..\\foo", "___foo")]
    [InlineData("foo/bar", "foo_bar")]
    [InlineData("foo\\bar", "foo_bar")]
    [InlineData(".", "_")]
    [InlineData("..", "__")]
    public void Sanitize_PathTraversalAttempts_AreReplaced(string input, string expected)
    {
        var result = RunCache.Sanitize(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Sanitize_MultipleInvalidCharacters_ReplacesAllWithUnderscores()
    {
         var invalidChars = Path.GetInvalidFileNameChars();
         if (invalidChars.Length >= 2)
         {
             var input = $"a{invalidChars[0]}b{invalidChars[1]}c";
             var result = RunCache.Sanitize(input);
             Assert.Equal("a_b_c", result);
         }
    }

    [Fact]
    public void Sanitize_OnlyInvalidCharacters_ReturnsStringOfUnderscores()
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        if (invalidChars.Length > 0)
        {
            var input = new string(invalidChars[0], 5);
            var result = RunCache.Sanitize(input);
            Assert.Equal("_____", result);
        }
    }

    [Fact]
    public async Task SetAndTryGetAsync_StoresAndRetrievesRun()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            await using var cache = await RunCache.LoadAsync("testApp", "testWorkflow", tempDir);

            var run = new CachedRun("Succeeded", DateTimeOffset.UtcNow, "{\"key\":\"value\"}");
            await cache.SetAsync("testRun", run);

            var retrieved = await cache.TryGetAsync("testRun");

            Assert.NotNull(retrieved);
            Assert.Equal(run.Status, retrieved.Status);
            Assert.Equal(run.Content, retrieved.Content);
            Assert.Equal(run.StartTime.ToString("o"), retrieved.StartTime.ToString("o")); // Exact ISO string comparison
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task TryGetAsync_NonExistingKey_ReturnsNull()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            await using var cache = await RunCache.LoadAsync("testApp", "testWorkflow", tempDir);
            var retrieved = await cache.TryGetAsync("nonExistingRun");
            Assert.Null(retrieved);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task LoadAsync_WithLegacyCacheFile_MigratesToDatabaseAndDeletesFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        var appName = "testApp";
        var workflowName = "testWorkflow";
        var runName = "run_123";

        var legacyFilePath = Path.Combine(tempDir, $"{RunCache.Sanitize(appName)}-{RunCache.Sanitize(workflowName)}.cache.json");

        var json = $$"""
        {
            "{{runName}}": {
                "status": "Failed",
                "startTime": "2023-10-27T10:00:00Z",
                "content": "error details"
            }
        }
        """;

        await File.WriteAllTextAsync(legacyFilePath, json);

        try
        {
            await using var cache = await RunCache.LoadAsync(appName, workflowName, tempDir);

            // File should be deleted after successful migration
            Assert.False(File.Exists(legacyFilePath));

            // DB should contain the migrated record
            var retrieved = await cache.TryGetAsync(runName);
            Assert.NotNull(retrieved);
            Assert.Equal("Failed", retrieved.Status);
            Assert.Equal("error details", retrieved.Content);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task LoadAsync_WithCorruptedLegacyCacheFile_IgnoresAndDeletesOrLeavesFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        var appName = "testApp";
        var workflowName = "testWorkflow";
        var legacyFilePath = Path.Combine(tempDir, $"{RunCache.Sanitize(appName)}-{RunCache.Sanitize(workflowName)}.cache.json");

        await File.WriteAllTextAsync(legacyFilePath, "{ invalid json }");

        try
        {
            await using var cache = await RunCache.LoadAsync(appName, workflowName, tempDir);

            // A corrupted file won't be successfully deserialized, so it shouldn't crash,
            // but it won't migrate anything. In our implementation, we catch the exception and log a warning,
            // so the file might not be deleted.
            var retrieved = await cache.TryGetAsync("any_run");
            Assert.Null(retrieved);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData("Succeeded")]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    [InlineData("Skipped")]
    [InlineData("TimedOut")]
    [InlineData("Aborted")]
    public void IsTerminal_TerminalStates_ReturnsTrue(string status)
    {
        Assert.True(RunCache.IsTerminal(status));
    }

    [Theory]
    [InlineData("Running")]
    [InlineData("Waiting")]
    [InlineData("Suspended")]
    [InlineData("Unknown")]
    [InlineData("")]
    [InlineData(null)]
    public void IsTerminal_NonTerminalStatesAndEdgeCases_ReturnsFalse(string? status)
    {
        Assert.False(RunCache.IsTerminal(status!));
    }

    [Theory]
    [InlineData("succeeded")]
    [InlineData("SUCCEEDED")]
    public void IsTerminal_CaseInsensitiveTerminalStates_ReturnsTrue(string? status)
    {
        Assert.True(RunCache.IsTerminal(status!));
    }
}
