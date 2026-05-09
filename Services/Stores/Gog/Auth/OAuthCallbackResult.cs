namespace Retromind.Services.Stores.Gog.Auth;

public sealed record OAuthCallbackResult(string Code, string? State);
