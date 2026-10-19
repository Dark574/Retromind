using System;

namespace Retromind.Services.Stores.Gog.Achievements;

public sealed record GogProgressSnapshot(
    GogGameProgress Progress,
    DateTimeOffset FetchedAtUtc,
    bool UsedCachedFallback);
