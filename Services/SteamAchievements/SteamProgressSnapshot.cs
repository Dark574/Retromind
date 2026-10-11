using System;

namespace Retromind.Services.SteamAchievements;

public sealed record SteamProgressSnapshot(
    SteamGameProgress Progress,
    DateTimeOffset FetchedAtUtc,
    bool UsedCachedFallback);
