namespace Retromind.Models;

/// <summary>
/// Non-secret settings for the Steam achievements integration.
/// The Steam Web API key is stored separately through ISecretStore.
/// </summary>
public sealed class SteamAchievementsSettings
{
    public bool Enabled { get; set; }

    public bool ShowInBigMode { get; set; } = true;

    /// <summary>
    /// The stable 64-bit Steam account identifier.
    /// It is stored as text to avoid parsing and serialization problems.
    /// </summary>
    public string? SteamId64 { get; set; }
}