using System;
using System.Net;

namespace Retromind.Services.Stores.Gog.Achievements;

public sealed class GogAchievementsApiException : Exception
{
    public HttpStatusCode? StatusCode { get; }

    public GogAchievementsApiException(string message)
        : base(message)
    {
    }

    public GogAchievementsApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public GogAchievementsApiException(HttpStatusCode statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }
}
