namespace Retromind.Services.Stores.Gog.Auth;

public sealed record PkceChallenge(string CodeVerifier, string CodeChallenge, string CodeChallengeMethod);
