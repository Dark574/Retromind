namespace Retromind.Models;

/// <summary>
/// Persisted state for a GOG DLC successfully installed by Retromind.
/// The available DLC catalog remains remote data and is not stored here.
/// </summary>
public sealed class GogDlcInstallationState
{
    public string ProductId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string? InstalledVersion { get; set; }
    public string? InstalledInstallerSignature { get; set; }
}
