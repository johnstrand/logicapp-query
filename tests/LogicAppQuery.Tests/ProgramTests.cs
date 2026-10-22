using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace LogicAppQuery.Tests;

public class ProgramTests
{
    [Theory]
    [InlineData("2026-10-16", "2026-10-16T23:59:59.9999000+00:00")]
    [InlineData("2024-02-29", "2024-02-29T23:59:59.9999000+00:00")]
    [InlineData("9999-12-31", "9999-12-31T23:59:59.9999000+00:00")]
    [InlineData("2026-10-16T00:00:00Z", "2026-10-16T00:00:00.0000000+00:00")]
    [InlineData("2026-10-16T12:30:00.1234567+02:00", "2026-10-16T12:30:00.1234567+02:00")]
    public void NormalizeEndDate_OnlyDateInputUsesEndOfDay(string input, string expected)
    {
        var parsed = DateTimeOffset.Parse(input, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal);

        var end = DateFilters.NormalizeEndDate(parsed, input);

        Assert.Equal(expected, end?.ToString("O"));
    }

    [Fact]
    public void NormalizeEndDate_NoEnd_RemainsNull()
    {
        Assert.Null(DateFilters.NormalizeEndDate(null, null));
    }

    [Fact]
    public void NormalizeEndDate_DateOnly_PreservesParsedOffset()
    {
        var parsed = new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.FromHours(2));

        var end = DateFilters.NormalizeEndDate(parsed, "2026-10-16");

        Assert.Equal("2026-10-16T23:59:59.9999000+02:00", end?.ToString("O"));
    }

    [Fact]
    public async Task Program_Main_WithHelpArgs_ReturnsZero()
    {
        var assembly = typeof(SearchCommand).Assembly;
        var entryPoint = assembly.EntryPoint;

        var result = entryPoint!.Invoke(null, new object[] { new[] { "--help" } });

        if (result is Task<int> task)
        {
            var code = await task;
            Assert.Equal(0, code);
        }
        else if (result is int code)
        {
            Assert.Equal(0, code);
        }
    }

    [Fact]
    public async Task Program_Main_WithMissingArgs_ReturnsNonZero()
    {
        var assembly = typeof(SearchCommand).Assembly;
        var entryPoint = assembly.EntryPoint;

        var result = entryPoint!.Invoke(null, new object[] { Array.Empty<string>() });

        if (result is Task<int> task)
        {
            var code = await task;
            Assert.NotEqual(0, code);
        }
        else if (result is int code)
        {
            Assert.NotEqual(0, code);
        }
    }
}
