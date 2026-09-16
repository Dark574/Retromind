using System;
using System.Threading;
using System.Threading.Tasks;

namespace Retromind.Services.RetroAchievements;

/// <summary>
/// Coordinates the complete RetroAchievements identity lookup for one game file.
/// </summary>
public sealed class RetroAchievementsGameIdentificationService
{
    private readonly IRetroAchievementsHashService _hashService;
    private readonly RetroAchievementsGameCatalogService _catalogService;
    private readonly RetroAchievementsAccountService _accountService;

    public RetroAchievementsGameIdentificationService(
        IRetroAchievementsHashService hashService,
        RetroAchievementsGameCatalogService catalogService,
        RetroAchievementsAccountService accountService)
    {
        _hashService = hashService ?? throw new ArgumentNullException(nameof(hashService));
        _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
        _accountService = accountService ?? throw new ArgumentNullException(nameof(accountService));
    }

    public async Task<RetroAchievementsIdentificationResult> IdentifyAsync(
        string gameSystemId,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameSystemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var apiKey = await _accountService
            .GetApiKeyAsync(cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "No RetroAchievements API key is configured.");
        }

        var gameHash = await _hashService
            .CalculateAsync(gameSystemId, filePath, cancellationToken)
            .ConfigureAwait(false);
        var game = await _catalogService
            .FindByHashAsync(gameHash.ConsoleId, gameHash.Hash, apiKey, cancellationToken)
            .ConfigureAwait(false);

        return new RetroAchievementsIdentificationResult(gameHash, game);
    }
}
