namespace Retromind.Services.RetroAchievements;

/// <summary>
/// Result of hashing a game file and resolving that hash against the
/// RetroAchievements game catalog. A null game is a valid "no match" result.
/// </summary>
public sealed record RetroAchievementsIdentificationResult(
    RetroAchievementsGameHash GameHash,
    RetroAchievementsGameCatalogEntry? Game)
{
    public bool IsMatch => Game != null;
}
