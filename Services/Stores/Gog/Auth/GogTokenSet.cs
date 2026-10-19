using System;

namespace Retromind.Services.Stores.Gog.Auth;

public sealed record GogTokenSet(
    string AccessToken,
    string RefreshToken,
    string UserId,
    DateTimeOffset AccessTokenExpiresAtUtc);
