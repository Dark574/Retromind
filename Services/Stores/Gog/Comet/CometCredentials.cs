using System.Text.Json.Serialization;

namespace Retromind.Services.Stores.Gog.Comet;

internal sealed record CometCredentials(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("username")] string Username)
{
    public override string ToString() =>
        "CometCredentials { AccessToken = <redacted>, RefreshToken = <redacted>, " +
        "UserId = <redacted>, Username = <redacted> }";
}
