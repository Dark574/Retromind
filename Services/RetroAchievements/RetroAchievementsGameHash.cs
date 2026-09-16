namespace Retromind.Services.RetroAchievements;

public sealed record RetroAchievementsGameHash(
    string GameSystemId,
    uint ConsoleId,
    string Hash);
