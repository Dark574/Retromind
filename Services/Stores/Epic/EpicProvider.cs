using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Models.Stores;
using Retromind.Services.Stores.Abstractions;
using Retromind.Services.Stores.Epic.Legendary;

namespace Retromind.Services.Stores.Epic;

internal sealed class EpicProvider : IStoreAuthProvider, IStoreLibraryProvider
{
    private readonly LegendaryCliClient _client;
    private readonly EpicLibraryService _libraryService;

    public EpicProvider(LegendaryCliClient client, EpicLibraryService libraryService)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
    }

    public string ProviderId => StoreProviderIds.Epic;

    public string DisplayName => "Epic Games";

    public StoreProviderCapabilities Capabilities =>
        StoreProviderCapabilities.Authentication |
        StoreProviderCapabilities.OwnedLibrary |
        StoreProviderCapabilities.Install |
        StoreProviderCapabilities.Update;

    public async Task<StoreAuthState> GetAuthStateAsync(CancellationToken ct = default)
    {
        var status = await _client.GetStatusAsync(ct).ConfigureAwait(false);
        return new StoreAuthState(status.IsAuthenticated, null);
    }

    public async Task<StoreAccountInfo> SignInInteractiveAsync(
        Func<StoreSignInChallenge, CancellationToken, Task<StoreSignInResponse?>>? challengeResolver = null,
        CancellationToken ct = default)
    {
        if (challengeResolver == null)
        {
            throw new InvalidOperationException(
                "Epic sign-in requires an authorization-code resolver.");
        }

        var response = await challengeResolver(
                new StoreSignInChallenge(
                    LegendaryCliClient.AuthorizationUri,
                    StoreSignInResponseKind.AuthorizationCode),
                ct)
            .ConfigureAwait(false);
        var authorizationCode = response?.AuthorizationCode;
        if (string.IsNullOrWhiteSpace(authorizationCode))
            throw new OperationCanceledException("Epic sign-in was canceled.");

        var status = await _client
            .AuthenticateWithAuthorizationCodeAsync(authorizationCode, ct)
            .ConfigureAwait(false);

        return new StoreAccountInfo("epic-user", status.AccountName, null);
    }

    public async Task<bool> TryRefreshSessionAsync(CancellationToken ct = default)
    {
        try
        {
            var status = await _client.GetValidatedStatusAsync(ct).ConfigureAwait(false);
            return status.IsAuthenticated;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LegendaryCliException ex)
        {
            Debug.WriteLine($"[Epic] Legendary session validation failed: {ex.Message}");
            return false;
        }
    }

    public Task SignOutAsync(bool forgetPersistentToken, CancellationToken ct = default)
    {
        if (!forgetPersistentToken)
        {
            throw new NotSupportedException(
                "Legendary cannot end the current session without deleting its stored Epic credentials.");
        }

        return _client.SignOutAsync(ct);
    }

    public Task<IReadOnlyList<StoreGameRecord>> GetOwnedGamesAsync(CancellationToken ct = default)
    {
        return _libraryService.GetOwnedGamesAsync(ct);
    }
}
