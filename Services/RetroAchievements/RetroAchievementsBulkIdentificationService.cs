using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Models;

namespace Retromind.Services.RetroAchievements;

public sealed record RetroAchievementsBulkIdentificationCandidate(
    MediaItem Item,
    string? GameSystemId,
    string? FilePath);

public enum RetroAchievementsBulkIdentificationOutcome
{
    Identified,
    NoMatch,
    SkippedAlreadyIdentified,
    SkippedMissingSystem,
    SkippedMissingFile,
    Failed
}

public sealed record RetroAchievementsBulkIdentificationItemResult(
    MediaItem Item,
    RetroAchievementsBulkIdentificationOutcome Outcome,
    RetroAchievementsGameIdentity? Identity = null,
    string? ErrorMessage = null);

public sealed record RetroAchievementsBulkIdentificationProgress(
    int CompletedCount,
    int TotalCount,
    RetroAchievementsBulkIdentificationItemResult ItemResult);

public sealed record RetroAchievementsBulkIdentificationResult(
    IReadOnlyList<RetroAchievementsBulkIdentificationItemResult> Items,
    int TotalCount,
    bool IsCancelled)
{
    public int IdentifiedCount => Count(RetroAchievementsBulkIdentificationOutcome.Identified);
    public int NoMatchCount => Count(RetroAchievementsBulkIdentificationOutcome.NoMatch);
    public int FailedCount => Count(RetroAchievementsBulkIdentificationOutcome.Failed);
    public int SkippedCount => Items.Count(item => item.Outcome is
        RetroAchievementsBulkIdentificationOutcome.SkippedAlreadyIdentified or
        RetroAchievementsBulkIdentificationOutcome.SkippedMissingSystem or
        RetroAchievementsBulkIdentificationOutcome.SkippedMissingFile);

    private int Count(RetroAchievementsBulkIdentificationOutcome outcome) =>
        Items.Count(item => item.Outcome == outcome);
}

/// <summary>
/// Runs the existing one-game identification pipeline sequentially for a batch.
/// Results are returned without mutating library items so callers can persist them atomically.
/// </summary>
public sealed class RetroAchievementsBulkIdentificationService
{
    private readonly IRetroAchievementsGameIdentificationService _identificationService;

    public RetroAchievementsBulkIdentificationService(
        IRetroAchievementsGameIdentificationService identificationService)
    {
        _identificationService = identificationService ?? throw new ArgumentNullException(nameof(identificationService));
    }

    public async Task<RetroAchievementsBulkIdentificationResult> IdentifyAsync(
        IReadOnlyList<RetroAchievementsBulkIdentificationCandidate> candidates,
        string apiKey,
        IProgress<RetroAchievementsBulkIdentificationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var results = new List<RetroAchievementsBulkIdentificationItemResult>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (cancellationToken.IsCancellationRequested)
                return new RetroAchievementsBulkIdentificationResult(results, candidates.Count, IsCancelled: true);

            var result = await IdentifyCandidateAsync(candidate, apiKey, cancellationToken)
                .ConfigureAwait(false);
            if (result == null)
                return new RetroAchievementsBulkIdentificationResult(results, candidates.Count, IsCancelled: true);

            results.Add(result);
            progress?.Report(new RetroAchievementsBulkIdentificationProgress(
                results.Count,
                candidates.Count,
                result));
        }

        return new RetroAchievementsBulkIdentificationResult(results, candidates.Count, IsCancelled: false);
    }

    private async Task<RetroAchievementsBulkIdentificationItemResult?> IdentifyCandidateAsync(
        RetroAchievementsBulkIdentificationCandidate candidate,
        string apiKey,
        CancellationToken cancellationToken)
    {
        if (candidate.Item.RetroAchievementsGame?.GameId > 0)
        {
            return new RetroAchievementsBulkIdentificationItemResult(
                candidate.Item,
                RetroAchievementsBulkIdentificationOutcome.SkippedAlreadyIdentified);
        }

        if (string.IsNullOrWhiteSpace(candidate.GameSystemId))
        {
            return new RetroAchievementsBulkIdentificationItemResult(
                candidate.Item,
                RetroAchievementsBulkIdentificationOutcome.SkippedMissingSystem);
        }

        if (string.IsNullOrWhiteSpace(candidate.FilePath))
        {
            return new RetroAchievementsBulkIdentificationItemResult(
                candidate.Item,
                RetroAchievementsBulkIdentificationOutcome.SkippedMissingFile);
        }

        try
        {
            var result = await _identificationService
                .IdentifyAsync(candidate.GameSystemId, candidate.FilePath, apiKey, cancellationToken)
                .ConfigureAwait(false);
            if (result.Game == null)
            {
                return new RetroAchievementsBulkIdentificationItemResult(
                    candidate.Item,
                    RetroAchievementsBulkIdentificationOutcome.NoMatch);
            }

            return new RetroAchievementsBulkIdentificationItemResult(
                candidate.Item,
                RetroAchievementsBulkIdentificationOutcome.Identified,
                new RetroAchievementsGameIdentity
                {
                    GameId = result.Game.GameId,
                    ConsoleId = result.GameHash.ConsoleId,
                    GameSystemId = result.GameHash.GameSystemId,
                    Hash = result.GameHash.Hash,
                    Title = result.Game.Title
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[RetroAchievements] Bulk identification failed for '{candidate.Item.Title}': {ex.Message}");
            return new RetroAchievementsBulkIdentificationItemResult(
                candidate.Item,
                RetroAchievementsBulkIdentificationOutcome.Failed,
                ErrorMessage: ex.Message);
        }
    }
}
