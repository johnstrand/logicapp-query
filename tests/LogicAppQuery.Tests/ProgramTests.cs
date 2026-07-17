using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace LogicAppQuery.Tests;

public class ProgramTests
{
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
