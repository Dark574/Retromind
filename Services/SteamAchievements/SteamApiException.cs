using System;
using System.Net;

namespace Retromind.Services.SteamAchievements;

public sealed class SteamApiException : Exception
{
    public HttpStatusCode? StatusCode { get; }

    public SteamApiException(string message)
        : base(message)
    {
    }

    public SteamApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SteamApiException(HttpStatusCode statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }
}