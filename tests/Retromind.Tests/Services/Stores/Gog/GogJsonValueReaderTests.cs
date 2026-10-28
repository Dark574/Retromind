using System.Text.Json;
using Retromind.Services.Stores.Gog;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogJsonValueReaderTests
{
    [Fact]
    public void ReadString_PreservesStringsAndReturnsNumberText()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "text": "  value  ",
              "number": 12345,
              "boolean": true,
              "nothing": null
            }
            """);
        var root = document.RootElement;

        Assert.Equal("  value  ", GogJsonValueReader.ReadString(root, "text"));
        Assert.Equal("12345", GogJsonValueReader.ReadString(root, "number"));
        Assert.Null(GogJsonValueReader.ReadString(root, "boolean"));
        Assert.Null(GogJsonValueReader.ReadString(root, "nothing"));
        Assert.Null(GogJsonValueReader.ReadString(root, "missing"));
    }

    [Fact]
    public void ReadInt32_AcceptsNumbersAndInvariantNumericStringsOnly()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "number": 12345,
              "text": "-42",
              "formatted": "1,234",
              "decimal": 1.5,
              "boolean": true
            }
            """);
        var root = document.RootElement;

        Assert.Equal(12345, GogJsonValueReader.ReadInt32(root, "number"));
        Assert.Equal(-42, GogJsonValueReader.ReadInt32(root, "text"));
        Assert.Null(GogJsonValueReader.ReadInt32(root, "formatted"));
        Assert.Null(GogJsonValueReader.ReadInt32(root, "decimal"));
        Assert.Null(GogJsonValueReader.ReadInt32(root, "boolean"));
        Assert.Null(GogJsonValueReader.ReadInt32(root, "missing"));
    }
}
