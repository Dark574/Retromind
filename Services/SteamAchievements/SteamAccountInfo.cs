namespace Retromind.Services.SteamAchievements;

/// <summary>
/// A locally detected Steam account from loginusers.vdf.
/// </summary>
public sealed record SteamAccountInfo(
    string SteamId64,
    string AccountName,
    string PersonaName,
    bool IsMostRecent)
{
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(PersonaName) &&
                !string.IsNullOrWhiteSpace(AccountName))
            {
                return $"{PersonaName} ({AccountName})";
            }

            if (!string.IsNullOrWhiteSpace(PersonaName))
                return PersonaName;

            if (!string.IsNullOrWhiteSpace(AccountName))
                return AccountName;

            return SteamId64;
        }
    }
}