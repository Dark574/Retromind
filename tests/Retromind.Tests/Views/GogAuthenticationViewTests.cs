using Retromind.Views;

namespace Retromind.Tests.Views;

public sealed class GogAuthenticationViewTests
{
    private static readonly Uri ExpectedCallback =
        new("https://embed.gog.com/on_login_success?origin=client");

    [Fact]
    public void IsCallbackUri_AcceptsExpectedEndpointWithOAuthParameters()
    {
        var candidate = new Uri(
            "https://embed.gog.com/on_login_success?origin=client&code=abc&state=xyz");

        Assert.True(GogAuthenticationView.IsCallbackUri(candidate, ExpectedCallback));
    }

    [Theory]
    [InlineData("http://embed.gog.com/on_login_success?origin=client&code=abc")]
    [InlineData("https://example.com/on_login_success?origin=client&code=abc")]
    [InlineData("https://embed.gog.com/other?origin=client&code=abc")]
    [InlineData("https://embed.gog.com:8443/on_login_success?origin=client&code=abc")]
    [InlineData("https://embed.gog.com/on_login_success?code=abc")]
    public void IsCallbackUri_RejectsDifferentEndpoint(string candidate)
    {
        Assert.False(GogAuthenticationView.IsCallbackUri(new Uri(candidate), ExpectedCallback));
    }
}
