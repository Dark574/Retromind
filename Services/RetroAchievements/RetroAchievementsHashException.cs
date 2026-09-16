using System;

namespace Retromind.Services.RetroAchievements;

public sealed class RetroAchievementsHashException : Exception
{
    public RetroAchievementsHashException(string message)
        : base(message)
    {
    }

    public RetroAchievementsHashException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
