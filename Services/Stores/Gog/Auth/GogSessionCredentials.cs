namespace Retromind.Services.Stores.Gog.Auth;

internal sealed record GogSessionCredentials(
    string AccessToken,
    string RefreshToken,
    string UserId,
    string Username)
{
    public override string ToString() =>
        "GogSessionCredentials { AccessToken = <redacted>, RefreshToken = <redacted>, " +
        "UserId = <redacted>, Username = <redacted> }";
}
