namespace Retromind.Models;

/// <summary>
/// Non-secret settings for the RetroAchievements account integration.
/// The Web API key is stored separately through ISecretStore.
/// </summary>
public sealed class RetroAchievementsSettings
{
    public bool Enabled { get; set; }

    public string? Username { get; set; }

    /// <summary>
    /// Stable user identifier returned by RetroAchievements after a successful
    /// profile lookup. Usernames may change, so later API requests should prefer it.
    /// </summary>
    public string? UserUlid { get; set; }
}
