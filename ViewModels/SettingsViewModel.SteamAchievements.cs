using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Models;
using Retromind.Services.SteamAchievements;
using Retromind.Services.Stores.Security;

namespace Retromind.ViewModels;

public partial class SettingsViewModel
{
    private readonly SteamAchievementsAccountService
        _steamAchievementsAccountService;

    private readonly SteamAccountDiscoveryService
        _steamAccountDiscoveryService;

    private bool _removeSteamApiKeyOnSave;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _steamAchievementsEnabled;

    [ObservableProperty]
    private bool _steamAchievementsShowInBigMode = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestSteamConnectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _steamAchievementsSteamId64 = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestSteamConnectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveSteamApiKeyCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _steamAchievementsApiKey = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestSteamConnectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveSteamApiKeyCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _hasStoredSteamApiKey;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestSteamConnectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveSteamApiKeyCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isSteamAchievementsBusy;

    [ObservableProperty]
    private string _steamAchievementsStatusText = string.Empty;

    [ObservableProperty]
    private SteamAccountInfo? _selectedSteamAccount;

    public ObservableCollection<SteamAccountInfo>
        DetectedSteamAccounts { get; } = [];

    public string SteamIntegrationTabTitle =>
        T("Settings_SteamIntegrationTabTitle", "Steam");

    public string SteamAchievementsSectionTitle =>
        T("Settings_SteamAchievementsSectionTitle", "Steam achievements");

    public string SteamAchievementsHint => T(
        "Settings_SteamAchievementsHint",
        "Connect your Steam account to show achievement progress for imported Steam games.");

    public string SteamAchievementsEnableText => T(
        "Settings_SteamAchievementsEnable",
        "Enable Steam achievements");

    public string SteamAchievementsShowInBigModeText => T(
        "Settings_SteamAchievementsShowInBigMode",
        "Show Steam achievement progress in BigMode");

    public string SteamDetectedAccountLabel => T(
        "Settings_SteamDetectedAccount",
        "Detected Steam account");

    public string SteamDetectedAccountHint => T(
        "Settings_SteamDetectedAccountHint",
        "Retromind reads locally signed-in accounts from Steam. Selecting an account fills in its SteamID64.");

    public string SteamId64Label =>
        T("Settings_SteamId64", "SteamID64");

    public string SteamId64Hint => T(
        "Settings_SteamId64Hint",
        "The SteamID64 contains 17 digits and can also be entered manually.");

    public string SteamApiKeyLabel =>
        T("Settings_SteamApiKey", "Web API key");

    public string SteamApiKeyHint => T(
        "Settings_SteamApiKeyHint",
        "Create a personal key at steamcommunity.com/dev/apikey. Steam currently requires the Steam Mobile Authenticator before a personal key can be registered. The key is stored in the system keyring when available and never written to Retromind settings or metadata backups. Without an available keyring, it remains available only for the current session. Leave the field empty to keep the stored key.");

    public string SteamTestConnectionText =>
        T("Settings_SteamTestConnection", "Test connection");

    public string SteamRemoveApiKeyText =>
        T("Settings_SteamRemoveApiKey", "Remove stored key");

    public IAsyncRelayCommand TestSteamConnectionCommand
        { get; private set; } = null!;

    public IRelayCommand RemoveSteamApiKeyCommand
        { get; private set; } = null!;

    public async Task InitializeSteamAchievementsAsync(
        CancellationToken cancellationToken = default)
    {
        SteamAchievementsStatusText = T(
            "Settings_SteamAchievementsStatusChecking",
            "Checking stored Steam account data...");

        var secretReadFailed = false;
        var accountDiscoveryFailed = false;

        try
        {
            var storedApiKey = await _steamAchievementsAccountService
                .GetApiKeyAsync(cancellationToken)
                .ConfigureAwait(true);
            if (_steamAchievementsAccountService.ApiKeyStorageStatus == SecretStorageStatus.Failed)
                throw new InvalidOperationException("The stored Steam Web API key could not be read.");

            HasStoredSteamApiKey =
                !string.IsNullOrWhiteSpace(storedApiKey);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            secretReadFailed = true;
            HasStoredSteamApiKey = false;
        }

        try
        {
            var detectedAccounts = await _steamAccountDiscoveryService
                .DiscoverAsync(cancellationToken)
                .ConfigureAwait(true);

            DetectedSteamAccounts.Clear();

            foreach (var account in detectedAccounts)
                DetectedSteamAccounts.Add(account);

            if (!string.IsNullOrWhiteSpace(
                    SteamAchievementsSteamId64))
            {
                SelectedSteamAccount =
                    DetectedSteamAccounts.FirstOrDefault(account =>
                        string.Equals(
                            account.SteamId64,
                            SteamAchievementsSteamId64,
                            StringComparison.Ordinal));
            }
            else
            {
                SelectedSteamAccount =
                    DetectedSteamAccounts.FirstOrDefault();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            accountDiscoveryFailed = true;
        }

        if (secretReadFailed)
        {
            SteamAchievementsStatusText = T(
                "Settings_SteamAchievementsStatusSecretReadFailed",
                "The stored Steam Web API key could not be read.");
        }
        else if (HasStoredSteamApiKey && _steamAchievementsAccountService.ApiKeyStorageStatus == SecretStorageStatus.SessionOnly)
        {
            SteamAchievementsStatusText = T("Settings_SteamAchievementsStatusSessionOnly",
                "The Steam Web API key is available only for this session. It was not saved in the system keyring.");
        }
        else if (accountDiscoveryFailed)
        {
            SteamAchievementsStatusText = T(
                "Settings_SteamAchievementsStatusDetectionFailed",
                "Local Steam accounts could not be detected. You can enter the SteamID64 manually.");
        }
        else if (DetectedSteamAccounts.Count == 0)
        {
            SteamAchievementsStatusText = T(
                "Settings_SteamAchievementsStatusNoAccount",
                "No local Steam account was detected. Enter the SteamID64 manually.");
        }
        else if (HasStoredSteamApiKey)
        {
            SteamAchievementsStatusText = T(
                "Settings_SteamAchievementsStatusStored",
                "A Steam Web API key is stored. You can test the connection or enter a replacement key.");
        }
        else
        {
            SteamAchievementsStatusText = T(
                "Settings_SteamAchievementsStatusNotConfigured",
                "No Steam Web API key is stored yet.");
        }
    }

    private bool CanTestSteamConnection()
    {
        return !IsSteamAchievementsBusy &&
               IsValidSteamId64(SteamAchievementsSteamId64) &&
               (!string.IsNullOrWhiteSpace(
                    SteamAchievementsApiKey) ||
                HasStoredSteamApiKey);
    }

    private async Task TestSteamConnectionAsync()
    {
        if (!CanTestSteamConnection())
            return;

        IsSteamAchievementsBusy = true;
        SteamAchievementsStatusText = T(
            "Settings_SteamAchievementsStatusConnecting",
            "Connecting to Steam...");

        try
        {
            var usesNewApiKey =
                !string.IsNullOrWhiteSpace(
                    SteamAchievementsApiKey);

            var profile = await _steamAchievementsAccountService
                .VerifyAsync(
                    SteamAchievementsSteamId64,
                    SteamAchievementsApiKey)
                .ConfigureAwait(true);

            SteamAchievementsSteamId64 = profile.SteamId64;

            var format = usesNewApiKey
                ? T(
                    "Settings_SteamAchievementsStatusConnectedNewKeyFormat",
                    "Connected as {0}. The new key will be stored when you save the settings.")
                : T(
                    "Settings_SteamAchievementsStatusConnectedFormat",
                    "Connected as {0}.");

            SteamAchievementsStatusText =
                string.Format(format, profile.PersonaName);
        }
        catch (OperationCanceledException)
        {
            SteamAchievementsStatusText = T(
                "Settings_SteamAchievementsStatusCanceled",
                "The Steam connection test was canceled.");
        }
        catch (SteamApiException ex)
        {
            SteamAchievementsStatusText =
                ex.StatusCode is HttpStatusCode.Unauthorized
                    or HttpStatusCode.Forbidden
                    ? T(
                        "Settings_SteamAchievementsStatusUnauthorized",
                        "The Steam Web API key was not accepted.")
                    : string.Format(
                        T(
                            "Settings_SteamAchievementsStatusFailedFormat",
                            "Connection failed: {0}"),
                        ex.Message);
        }
        catch
        {
            SteamAchievementsStatusText = T(
                "Settings_SteamAchievementsStatusUnexpectedFailure",
                "The Steam connection test failed unexpectedly.");
        }
        finally
        {
            IsSteamAchievementsBusy = false;
        }
    }

    private bool CanRemoveSteamApiKey()
    {
        return !IsSteamAchievementsBusy &&
               (HasStoredSteamApiKey ||
                !string.IsNullOrWhiteSpace(
                    SteamAchievementsApiKey));
    }

    private void RemoveSteamApiKey()
    {
        _removeSteamApiKeyOnSave = true;
        SteamAchievementsApiKey = string.Empty;
        HasStoredSteamApiKey = false;
        SteamAchievementsEnabled = false;

        SteamAchievementsStatusText = T(
            "Settings_SteamAchievementsStatusRemovePending",
            "Steam achievements were disabled. The stored Web API key will be removed when you save the settings.");
    }

    private bool CanSaveSteamAchievements()
    {
        if (IsSteamAchievementsBusy)
            return false;

        if (!SteamAchievementsEnabled)
            return true;

        return IsValidSteamId64(
                   SteamAchievementsSteamId64) &&
               (!string.IsNullOrWhiteSpace(
                    SteamAchievementsApiKey) ||
                HasStoredSteamApiKey);
    }

    private async Task ApplySteamAchievementsSecretChangeAsync()
    {
        if (_removeSteamApiKeyOnSave)
        {
            await _steamAchievementsAccountService.DeleteApiKeyAsync();
            return;
        }

        if (!string.IsNullOrWhiteSpace(SteamAchievementsApiKey))
        {
            await _steamAchievementsAccountService.StoreApiKeyAsync(SteamAchievementsApiKey);
            if (_steamAchievementsAccountService.ApiKeyStorageStatus == SecretStorageStatus.SessionOnly)
                SteamAchievementsStatusText = T("Settings_SteamAchievementsStatusSessionOnly",
                    "The Steam Web API key is available only for this session. It was not saved in the system keyring.");
        }
    }

    private void ApplySteamAchievementsSettings()
    {
        var settings = _appSettings.SteamAchievements ??=
            new SteamAchievementsSettings();

        settings.Enabled = SteamAchievementsEnabled;
        settings.ShowInBigMode =
            SteamAchievementsShowInBigMode;
        settings.SteamId64 =
            string.IsNullOrWhiteSpace(
                SteamAchievementsSteamId64)
                ? null
                : SteamAchievementsSteamId64.Trim();
    }

    private static bool IsValidSteamId64(string? value)
    {
        var candidate = value?.Trim();

        return candidate?.Length == 17 &&
               candidate.All(character =>
                   character is >= '0' and <= '9');
    }

    partial void OnSteamAchievementsSteamId64Changed(string value)
    {
        var matchingAccount =
            DetectedSteamAccounts.FirstOrDefault(account =>
                string.Equals(
                    account.SteamId64,
                    value.Trim(),
                    StringComparison.Ordinal));

        if (!Equals(SelectedSteamAccount, matchingAccount))
            SelectedSteamAccount = matchingAccount;
    }

    partial void OnSelectedSteamAccountChanged(
        SteamAccountInfo? value)
    {
        if (value != null)
            SteamAchievementsSteamId64 = value.SteamId64;
    }

    partial void OnSteamAchievementsApiKeyChanged(
        string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            _removeSteamApiKeyOnSave = false;
    }
}
