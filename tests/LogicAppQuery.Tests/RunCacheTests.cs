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
    public void TryGet_ExistingKey_ReturnsTrueAndRun()
    {
        var cache = RunCache.Load("testApp", "testWorkflow_ExistingKey");
        var run = new CachedRun("Succeeded", DateTimeOffset.UtcNow, "{}");
        cache.Set("testRun", run);

        var result = cache.TryGet("testRun", out var retrievedRun);

        Assert.True(result);
        Assert.NotNull(retrievedRun);
        Assert.Equal(run.Status, retrievedRun.Status);
        Assert.Equal(run.Content, retrievedRun.Content);
        Assert.Equal(run.StartTime, retrievedRun.StartTime);
    }

    [Fact]
    public void TryGet_NonExistingKey_ReturnsFalseAndNull()
    {
        var cache = RunCache.Load("testApp", "testWorkflow_NonExistingKey");
        var result = cache.TryGet("nonExistingRun", out var retrievedRun);

        Assert.False(result);
        Assert.Null(retrievedRun);
    }

    [Fact]
    public void TryGet_NullKey_ThrowsArgumentNullException()
    {
        var cache = RunCache.Load("testApp", "testWorkflow_NullKey");
        Assert.Throws<ArgumentNullException>(() => cache.TryGet(null!, out _));
    }
}
