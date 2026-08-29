using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Models.Stores;
using Retromind.Services.Stores.Abstractions;
using Retromind.Services.Stores.Gog.Auth;

namespace Retromind.Services.Stores.Gog;

public sealed class GogProvider : IStoreAuthProvider, IStoreLibraryProvider
{
    private readonly GogAuthService _authService;
    private readonly GogLibraryService _libraryService;

    public GogProvider(
        GogAuthService authService,
        GogLibraryService libraryService)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
    }

    public string ProviderId => "gog";

    public string DisplayName => "GOG";

    public StoreProviderCapabilities Capabilities =>
        StoreProviderCapabilities.Auth |
        StoreProviderCapabilities.Library;

    public Task<StoreAuthState> GetAuthStateAsync(CancellationToken ct = default)
    {
        return _authService.GetAuthStateAsync(ct);
    }

    public Task<StoreAccountInfo> SignInInteractiveAsync(
        Func<Uri, CancellationToken, Task<Uri?>>? callbackUriResolver = null,
        CancellationToken ct = default)
    {
        return _authService.SignInInteractiveAsync(callbackUriResolver, ct);
    }

    public Task<bool> TryRefreshSessionAsync(CancellationToken ct = default)
    {
        return _authService.TryRefreshSessionAsync(ct);
    }

    public Task SignOutAsync(bool forgetPersistentToken, CancellationToken ct = default)
    {
        return _authService.SignOutAsync(forgetPersistentToken, ct);
    }

    public Task<IReadOnlyList<StoreGameRecord>> GetOwnedGamesAsync(CancellationToken ct = default)
    {
        return _libraryService.GetOwnedGamesAsync(ct);
    }
}
