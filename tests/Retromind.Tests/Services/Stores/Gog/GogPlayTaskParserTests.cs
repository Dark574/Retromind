using System.Text.Json;
using Retromind.Services.Stores.Gog;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogPlayTaskParserTests
{
    [Fact]
    public void Parse_ReadsTasksAndNormalizesArgumentArrays()
    {
        using var json = JsonDocument.Parse(
            """
            {
              "playTasks": [
                {
                  "path": "fallback.exe",
                  "arguments": "--direct value",
                  "workingDir": "game"
                },
                {
                  "path": "primary.exe",
                  "arguments": ["--flag", "value with space", "quoted\"value", "", 42],
                  "workingDir": "bin",
                  "isPrimary": true
                },
                {
                  "arguments": ["missing-path"]
                }
              ]
            }
            """);

        var result = GogPlayTaskParser.Parse(json.RootElement.GetProperty("playTasks"));

        Assert.Collection(
            result,
            task =>
            {
                Assert.Equal("fallback.exe", task.Path);
                Assert.Equal("--direct value", task.Arguments);
                Assert.Equal("game", task.WorkingDirectory);
                Assert.False(task.IsPrimary);
            },
            task =>
            {
                Assert.Equal("primary.exe", task.Path);
                Assert.Equal("--flag \"value with space\" \"quoted\\\"value\"", task.Arguments);
                Assert.Equal("bin", task.WorkingDirectory);
                Assert.True(task.IsPrimary);
            });
    }

    [Fact]
    public void SelectPrimary_PrefersMarkedTaskAndFallsBackToFirst()
    {
        var first = new GogPlayTaskInfo("first.exe", null, null, false);
        var primary = new GogPlayTaskInfo("primary.exe", null, null, true);

        Assert.Same(primary, GogPlayTaskParser.SelectPrimary([first, primary]));
        Assert.Same(first, GogPlayTaskParser.SelectPrimary([first]));
        Assert.Null(GogPlayTaskParser.SelectPrimary([]));
    }

    [Fact]
    public void Parse_NonArrayReturnsEmptyList()
    {
        using var json = JsonDocument.Parse("""{ "playTasks": null }""");

        var result = GogPlayTaskParser.Parse(json.RootElement.GetProperty("playTasks"));

        Assert.Empty(result);
    }
}
