using Retromind.Models.Stores;
using Retromind.Services.Stores.Abstractions;
using Retromind.Services.Stores.Epic;
using Retromind.Services.Stores.Epic.Legendary;

namespace Retromind.Tests.Services.Stores.Epic;

public sealed class EpicProviderTests
{
    [Fact]
    public void Provider_AdvertisesOnlyImplementedCapability()
    {
        var provider = CreateProvider(new FakeProcessRunner());

        Assert.Equal(StoreProviderIds.Epic, provider.ProviderId);
        Assert.Equal("Epic Games", provider.DisplayName);
        Assert.Equal(
            StoreProviderCapabilities.Authentication | StoreProviderCapabilities.OwnedLibrary,
            provider.Capabilities);
    }

    [Fact]
    public async Task GetAuthStateAsync_UsesOfflineLegendaryStatus()
    {
        var runner = new FakeProcessRunner(AuthenticatedStatus());
        var provider = CreateProvider(runner);

        var state = await provider.GetAuthStateAsync();

        Assert.True(state.IsAuthenticated);
        Assert.Null(state.AccessTokenExpiresAtUtc);
        Assert.Equal(["status", "--offline", "--json"], Assert.Single(runner.Commands).Arguments);
    }

    [Fact]
    public async Task SignInInteractiveAsync_RequestsAuthorizationCodeAndReturnsAccount()
    {
        var runner = new FakeProcessRunner(
            new LegendaryCliResult(0, string.Empty, string.Empty),
            AuthenticatedStatus());
        var provider = CreateProvider(runner);
        StoreSignInChallenge? receivedChallenge = null;

        var account = await provider.SignInInteractiveAsync((challenge, _) =>
        {
            receivedChallenge = challenge;
            return Task.FromResult<StoreSignInResponse?>(
                StoreSignInResponse.FromAuthorizationCode("one-time-code"));
        });

        Assert.NotNull(receivedChallenge);
        Assert.Equal(LegendaryCliClient.AuthorizationUri, receivedChallenge.AuthorizationUri);
        Assert.Equal(StoreSignInResponseKind.AuthorizationCode, receivedChallenge.ResponseKind);
        Assert.Equal("Example Player", account.DisplayName);
        Assert.Equal(["auth", "--code", "one-time-code"], runner.Commands[0].Arguments);
        Assert.Equal(["status", "--json"], runner.Commands[1].Arguments);
    }

    [Fact]
    public async Task SignInInteractiveAsync_TreatsMissingCodeAsCancellation()
    {
        var provider = CreateProvider(new FakeProcessRunner());

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            provider.SignInInteractiveAsync((_, _) =>
                Task.FromResult<StoreSignInResponse?>(null)));
    }

    [Fact]
    public async Task TryRefreshSessionAsync_ReturnsFalseWhenOnlineValidationFails()
    {
        var provider = CreateProvider(
            new FakeProcessRunner(new LegendaryCliResult(1, string.Empty, "Login failed")));

        var refreshed = await provider.TryRefreshSessionAsync();

        Assert.False(refreshed);
    }

    [Fact]
    public async Task SignOutAsync_RequiresPersistentCredentialDeletion()
    {
        var provider = CreateProvider(new FakeProcessRunner());

        await Assert.ThrowsAsync<NotSupportedException>(() => provider.SignOutAsync(false));
    }

    private static EpicProvider CreateProvider(FakeProcessRunner runner)
    {
        var client = new LegendaryCliClient(runner);
        return new EpicProvider(client, new EpicLibraryService(client));
    }

    private static LegendaryCliResult AuthenticatedStatus() => new(
        0,
        """
        {
          "account": "Example Player",
          "games_available": 42,
          "games_installed": 3,
          "egl_sync_enabled": false,
          "config_directory": "/portable/Tools/Legendary/Data"
        }
        """,
        string.Empty);

    private sealed class FakeProcessRunner(params LegendaryCliResult[] results) : ILegendaryCliProcessRunner
    {
        private readonly Queue<LegendaryCliResult> _results = new(results);

        internal List<LegendaryCliCommand> Commands { get; } = [];

        public Task<LegendaryCliResult> RunAsync(
            LegendaryCliCommand command,
            CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(_results.Dequeue());
        }
    }
}
