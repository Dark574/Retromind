using System;

namespace Retromind.Models.Stores;

public sealed record StoreAuthState(bool IsAuthenticated, DateTimeOffset? AccessTokenExpiresAtUtc);
