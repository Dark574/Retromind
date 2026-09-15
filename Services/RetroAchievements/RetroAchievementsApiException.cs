using System;
using System.Net;

namespace Retromind.Services.RetroAchievements;

public sealed class RetroAchievementsApiException : Exception
{
    public HttpStatusCode? StatusCode { get; }

    public RetroAchievementsApiException(string message)
        : base(message)
    {
    }

    public RetroAchievementsApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public RetroAchievementsApiException(HttpStatusCode statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }
}
