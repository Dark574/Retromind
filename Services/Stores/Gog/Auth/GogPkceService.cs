using System;
using System.Security.Cryptography;
using System.Text;

namespace Retromind.Services.Stores.Gog.Auth;

public sealed class GogPkceService
{
    public PkceChallenge CreateChallenge()
    {
        var verifierBytes = RandomNumberGenerator.GetBytes(32);
        var codeVerifier = Base64UrlEncode(verifierBytes);

        var challengeBytes = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        var codeChallenge = Base64UrlEncode(challengeBytes);

        return new PkceChallenge(codeVerifier, codeChallenge, "S256");
    }

    private static string Base64UrlEncode(byte[] data)
    {
        return Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
