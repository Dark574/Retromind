namespace Retromind.Services.Stores.Gog.Auth;

internal sealed record GogSessionCredentials(
    string AccessToken,
    string RefreshToken,
    string AccountId,
    string Username)
{
    public override string ToString() =>
        "GogSessionCredentials { AccessToken = <redacted>, RefreshToken = <redacted>, " +
        "AccountId = <redacted>, Username = <redacted> }";
}
