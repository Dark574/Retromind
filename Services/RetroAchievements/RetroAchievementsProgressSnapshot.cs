using System;

namespace Retromind.Services.RetroAchievements;

public sealed record RetroAchievementsProgressSnapshot(
    RetroAchievementsGameProgress Progress,
    DateTimeOffset FetchedAtUtc,
    bool UsedCachedFallback);
