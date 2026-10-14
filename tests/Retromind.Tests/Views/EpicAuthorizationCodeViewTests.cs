using Retromind.Views;

namespace Retromind.Tests.Views;

public sealed class EpicAuthorizationCodeViewTests
{
    [Theory]
    [InlineData("example-code", "example-code")]
    [InlineData("  example-code  ", "example-code")]
    [InlineData("\"example-code\"", "example-code")]
    [InlineData("{\"authorizationCode\":\"example-code\",\"sid\":null}", "example-code")]
    [InlineData("{\"AUTHORIZATIONCODE\":\"example-code\"}", "example-code")]
    public void TryExtractAuthorizationCode_AcceptsCodeOrEpicJson(string input, string expected)
    {
        var parsed = EpicAuthorizationCodeView.TryExtractAuthorizationCode(input, out var code);

        Assert.True(parsed);
        Assert.Equal(expected, code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("two words")]
    [InlineData("{}")]
    [InlineData("{\"authorizationCode\":null}")]
    [InlineData("{not-json}")]
    public void TryExtractAuthorizationCode_RejectsInvalidInput(string? input)
    {
        var parsed = EpicAuthorizationCodeView.TryExtractAuthorizationCode(input, out var code);

        Assert.False(parsed);
        Assert.Empty(code);
    }
}
