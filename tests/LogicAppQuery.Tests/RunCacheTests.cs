using System;
using System.IO;
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
    public void Load_WithCorruptedCacheFile_ReturnsEmptyCache()
    {
        var appName = "testAppErrorPath";
        var workflowName = "testWorkflowErrorPath";

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LogicAppQuery");
        Directory.CreateDirectory(dir);

        var fileName = $"{RunCache.Sanitize(appName)}-{RunCache.Sanitize(workflowName)}.cache.json";
        var filePath = Path.Combine(dir, fileName);

        File.WriteAllText(filePath, "{ invalid json }");

        try
        {
            var cache = RunCache.Load(appName, workflowName);
            Assert.NotNull(cache);
            Assert.False(cache.TryGet("any", out _));
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    [Fact]
    public void Set_NewRun_UpdatesCacheAndDirtyFlag()
    {
        var cache = new RunCache("dummy.json", new System.Collections.Generic.Dictionary<string, CachedRun>());
        var runName = "run1";
        var run = new CachedRun("Succeeded", DateTimeOffset.UtcNow, "content1");

        Assert.False(cache.IsDirty);

        cache.Set(runName, run);

        Assert.True(cache.IsDirty);
        Assert.True(cache.TryGet(runName, out var retrievedRun));
        Assert.Equal(run, retrievedRun);
    }

    [Fact]
    public void Set_ExistingRun_UpdatesCacheAndDirtyFlag()
    {
        var runName = "run1";
        var initialRun = new CachedRun("Failed", DateTimeOffset.UtcNow.AddMinutes(-5), "initial_content");
        var runs = new System.Collections.Generic.Dictionary<string, CachedRun> { { runName, initialRun } };
        var cache = new RunCache("dummy.json", runs);

        var updatedRun = new CachedRun("Succeeded", DateTimeOffset.UtcNow, "updated_content");

        Assert.False(cache.IsDirty);

        cache.Set(runName, updatedRun);

        Assert.True(cache.IsDirty);
        Assert.True(cache.TryGet(runName, out var retrievedRun));
        Assert.Equal(updatedRun, retrievedRun);
        Assert.NotEqual(initialRun, retrievedRun);
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
    [InlineData("succeeded")] // Case sensitivity check
    [InlineData("")]
    [InlineData(null)]
    public void IsTerminal_NonTerminalStatesAndEdgeCases_ReturnsFalse(string? status)
    {
        Assert.False(RunCache.IsTerminal(status!));
    }
}
