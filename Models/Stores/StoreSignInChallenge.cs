using System;

namespace Retromind.Models.Stores;

public enum StoreSignInResponseKind
{
    CallbackUri,
    AuthorizationCode
}

public sealed record StoreSignInChallenge(
    Uri AuthorizationUri,
    StoreSignInResponseKind ResponseKind);

public sealed record StoreSignInResponse(Uri? CallbackUri, string? AuthorizationCode)
{
    public static StoreSignInResponse FromCallbackUri(Uri callbackUri)
    {
        ArgumentNullException.ThrowIfNull(callbackUri);
        return new StoreSignInResponse(callbackUri, null);
    }

    public static StoreSignInResponse FromAuthorizationCode(string authorizationCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationCode);
        return new StoreSignInResponse(null, authorizationCode);
    }
}
