namespace Retromind.Services.RetroAchievements;

public sealed record RetroAchievementsUserProfile(
    string Username,
    string UserUlid,
    int TotalPoints,
    int TotalSoftcorePoints,
    int TotalTruePoints,
    string? UserPicturePath);
