using Retromind.Views;

namespace Retromind.Tests.Views;

public sealed class GogBrowserCallbackViewTests
{
    private static readonly Uri ExpectedCallback =
        new("https://embed.gog.com/on_login_success?origin=client");

    [Fact]
    public void ValidateCallbackInput_AcceptsExpectedGogCallback()
    {
        var result = GogBrowserCallbackView.ValidateCallbackInput(
            "  https://embed.gog.com/on_login_success?origin=client&code=abc&state=xyz  ",
            ExpectedCallback,
            out var callbackUri);

        Assert.Equal(GogBrowserCallbackView.CallbackInputValidation.Valid, result);
        Assert.Equal(
            "https://embed.gog.com/on_login_success?origin=client&code=abc&state=xyz",
            callbackUri?.AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a URL")]
    public void ValidateCallbackInput_RejectsInvalidUrl(string? input)
    {
        var result = GogBrowserCallbackView.ValidateCallbackInput(
            input,
            ExpectedCallback,
            out var callbackUri);

        Assert.Equal(GogBrowserCallbackView.CallbackInputValidation.InvalidUri, result);
        Assert.Null(callbackUri);
    }

    [Theory]
    [InlineData("https://example.com/on_login_success?origin=client&code=abc")]
    [InlineData("https://embed.gog.com/on_login_success?code=abc")]
    public void ValidateCallbackInput_RejectsUnexpectedEndpoint(string input)
    {
        var result = GogBrowserCallbackView.ValidateCallbackInput(
            input,
            ExpectedCallback,
            out var callbackUri);

        Assert.Equal(GogBrowserCallbackView.CallbackInputValidation.UnexpectedEndpoint, result);
        Assert.Null(callbackUri);
    }
}
