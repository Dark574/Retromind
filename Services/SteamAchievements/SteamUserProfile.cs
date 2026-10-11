namespace Retromind.Services.SteamAchievements;

public sealed record SteamUserProfile(
    string SteamId64,
    string PersonaName,
    string? ProfileUrl,
    string? AvatarUrl);