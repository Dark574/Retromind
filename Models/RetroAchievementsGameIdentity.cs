namespace Retromind.Models;

/// <summary>
/// Persisted RetroAchievements identity of a specific game file.
/// Account credentials and achievement progress are intentionally not stored here.
/// </summary>
public sealed class RetroAchievementsGameIdentity
{
    public int GameId { get; set; }
    public uint ConsoleId { get; set; }
    public string GameSystemId { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
}
