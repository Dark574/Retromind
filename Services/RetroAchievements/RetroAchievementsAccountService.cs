using System;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Services.Stores.Security;

namespace Retromind.Services.RetroAchievements;

/// <summary>
/// Coordinates RetroAchievements account verification and secret storage.
/// </summary>
public sealed class RetroAchievementsAccountService
{
    private static readonly SecretKey WebApiKey =
        new("retromind:retroachievements", "web-api-key");

    private readonly RetroAchievementsApiClient _apiClient;
    private readonly ISecretStore _secretStore;

    public RetroAchievementsAccountService(
        RetroAchievementsApiClient apiClient,
        ISecretStore secretStore)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
    }

    public Task<string?> GetApiKeyAsync(CancellationToken cancellationToken = default)
        => _secretStore.GetAsync(WebApiKey, cancellationToken);

    public Task StoreApiKeyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        return _secretStore.SetAsync(WebApiKey, apiKey.Trim(), cancellationToken);
    }

    public Task DeleteApiKeyAsync(CancellationToken cancellationToken = default)
        => _secretStore.DeleteAsync(WebApiKey, cancellationToken);

    public async Task<RetroAchievementsUserProfile> VerifyAsync(
        string username,
        string? pendingApiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        var apiKey = string.IsNullOrWhiteSpace(pendingApiKey)
            ? await GetApiKeyAsync(cancellationToken).ConfigureAwait(false)
            : pendingApiKey.Trim();

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("No RetroAchievements API key is configured.");

        return await _apiClient
            .GetUserProfileAsync(username.Trim(), apiKey, cancellationToken)
            .ConfigureAwait(false);
    }
}
