namespace Retromind.Services.Stores.Gog.Auth;

internal sealed record GogGameplayCredentials(
    string AccessToken,
    string UserId)
{
    public override string ToString() =>
        "GogGameplayCredentials { AccessToken = <redacted>, UserId = <redacted> }";
}
