using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Retromind.Services.RetroAchievements;

namespace Retromind.ViewModels;

public partial class SettingsViewModel
{
    private readonly RetroAchievementsAccountService _retroAchievementsAccountService;
    private bool _removeRetroAchievementsApiKeyOnSave;
    private string? _verifiedRetroAchievementsUsername;
    private string? _verifiedRetroAchievementsUserUlid;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _retroAchievementsEnabled;

    [ObservableProperty]
    private bool _retroAchievementsShowInBigMode = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestRetroAchievementsConnectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _retroAchievementsUsername = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestRetroAchievementsConnectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveRetroAchievementsApiKeyCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _retroAchievementsApiKey = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestRetroAchievementsConnectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveRetroAchievementsApiKeyCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _hasStoredRetroAchievementsApiKey;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestRetroAchievementsConnectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveRetroAchievementsApiKeyCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isRetroAchievementsBusy;

    [ObservableProperty]
    private string _retroAchievementsStatusText = string.Empty;

    public string RetroAchievementsTabTitle =>
        T("Settings_RetroAchievementsTabTitle", "RetroAchievements");

    public string RetroAchievementsSectionTitle =>
        T("Settings_RetroAchievementsSectionTitle", "RetroAchievements account");

    public string RetroAchievementsHint => T(
        "Settings_RetroAchievementsHint",
        "Connect your RetroAchievements account to show achievement progress for identified games.");

    public string RetroAchievementsEnableText => T(
        "Settings_RetroAchievementsEnable",
        "Enable RetroAchievements integration");

    public string RetroAchievementsShowInBigModeText => T(
        "Settings_RetroAchievementsShowInBigMode",
        "Show RetroAchievements progress in BigMode");

    public string RetroAchievementsUsernameLabel =>
        T("Settings_RetroAchievementsUsername", "Username");

    public string RetroAchievementsApiKeyLabel =>
        T("Settings_RetroAchievementsApiKey", "Web API key");

    public string RetroAchievementsApiKeyHint => T(
        "Settings_RetroAchievementsApiKeyHint",
        "The key is stored in the system keyring when available and never written to Retromind settings or metadata backups. Without an available keyring, it remains available only for the current session. Leave the field empty to keep the stored key.");

    public string RetroAchievementsTestConnectionText =>
        T("Settings_RetroAchievementsTestConnection", "Test connection");

    public string RetroAchievementsRemoveApiKeyText =>
        T("Settings_RetroAchievementsRemoveApiKey", "Remove stored key");

    public CommunityToolkit.Mvvm.Input.IAsyncRelayCommand TestRetroAchievementsConnectionCommand { get; private set; } = null!;
    public CommunityToolkit.Mvvm.Input.IRelayCommand RemoveRetroAchievementsApiKeyCommand { get; private set; } = null!;

    public async Task InitializeRetroAchievementsAsync(CancellationToken cancellationToken = default)
    {
        RetroAchievementsStatusText = T(
            "Settings_RetroAchievementsStatusChecking",
            "Checking stored account data...");

        try
        {
            var storedApiKey = await _retroAchievementsAccountService
                .GetApiKeyAsync(cancellationToken)
                .ConfigureAwait(true);

            HasStoredRetroAchievementsApiKey = !string.IsNullOrWhiteSpace(storedApiKey);
            RetroAchievementsStatusText = HasStoredRetroAchievementsApiKey
                ? T(
                    "Settings_RetroAchievementsStatusStored",
                    "A Web API key is stored. You can test the connection or enter a replacement key.")
                : T(
                    "Settings_RetroAchievementsStatusNotConfigured",
                    "No Web API key is stored yet.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            HasStoredRetroAchievementsApiKey = false;
            RetroAchievementsStatusText = T(
                "Settings_RetroAchievementsStatusSecretReadFailed",
                "The stored Web API key could not be read.");
        }
    }

    private bool CanTestRetroAchievementsConnection()
    {
        return !IsRetroAchievementsBusy &&
               !string.IsNullOrWhiteSpace(RetroAchievementsUsername) &&
               (!string.IsNullOrWhiteSpace(RetroAchievementsApiKey) ||
                HasStoredRetroAchievementsApiKey);
    }

    private async Task TestRetroAchievementsConnectionAsync()
    {
        if (!CanTestRetroAchievementsConnection())
            return;

        IsRetroAchievementsBusy = true;
        RetroAchievementsStatusText = T(
            "Settings_RetroAchievementsStatusConnecting",
            "Connecting to RetroAchievements...");

        try
        {
            var usesNewApiKey = !string.IsNullOrWhiteSpace(RetroAchievementsApiKey);
            var profile = await _retroAchievementsAccountService
                .VerifyAsync(RetroAchievementsUsername, RetroAchievementsApiKey)
                .ConfigureAwait(true);

            RetroAchievementsUsername = profile.Username;
            _verifiedRetroAchievementsUsername = profile.Username;
            _verifiedRetroAchievementsUserUlid = profile.UserUlid;

            var statusFormat = usesNewApiKey
                ? T(
                    "Settings_RetroAchievementsStatusConnectedNewKeyFormat",
                    "Connected as {0}. Hardcore points: {1:N0}. The new key will be stored when you save the settings.")
                : T(
                    "Settings_RetroAchievementsStatusConnectedFormat",
                    "Connected as {0}. Hardcore points: {1:N0}.");
            RetroAchievementsStatusText = string.Format(
                statusFormat,
                profile.Username,
                profile.TotalPoints);
        }
        catch (OperationCanceledException)
        {
            RetroAchievementsStatusText = T(
                "Settings_RetroAchievementsStatusCanceled",
                "The connection test was canceled.");
        }
        catch (RetroAchievementsApiException ex)
        {
            RetroAchievementsStatusText = ex.StatusCode == HttpStatusCode.Unauthorized
                ? T(
                    "Settings_RetroAchievementsStatusUnauthorized",
                    "The Web API key was not accepted (HTTP 401). Make sure you copied the Web API key, not an emulator or Connect API key.")
                : string.Format(
                    T(
                        "Settings_RetroAchievementsStatusFailedFormat",
                        "Connection failed: {0}"),
                    ex.Message);
        }
        catch
        {
            RetroAchievementsStatusText = T(
                "Settings_RetroAchievementsStatusUnexpectedFailure",
                "The RetroAchievements connection test failed unexpectedly.");
        }
        finally
        {
            IsRetroAchievementsBusy = false;
        }
    }

    private bool CanRemoveRetroAchievementsApiKey()
    {
        return !IsRetroAchievementsBusy &&
               (HasStoredRetroAchievementsApiKey ||
                !string.IsNullOrWhiteSpace(RetroAchievementsApiKey));
    }

    private void RemoveRetroAchievementsApiKey()
    {
        _removeRetroAchievementsApiKeyOnSave = true;
        RetroAchievementsApiKey = string.Empty;
        HasStoredRetroAchievementsApiKey = false;
        RetroAchievementsEnabled = false;
        _verifiedRetroAchievementsUsername = null;
        _verifiedRetroAchievementsUserUlid = null;
        RetroAchievementsStatusText = T(
            "Settings_RetroAchievementsStatusRemovePending",
            "RetroAchievements was disabled. The stored Web API key will be removed when you save the settings.");
    }

    private bool CanSaveRetroAchievements()
    {
        if (IsRetroAchievementsBusy)
            return false;

        if (!RetroAchievementsEnabled)
            return true;

        return !string.IsNullOrWhiteSpace(RetroAchievementsUsername) &&
               (!string.IsNullOrWhiteSpace(RetroAchievementsApiKey) ||
                HasStoredRetroAchievementsApiKey);
    }

    private async Task<bool> SaveRetroAchievementsAsync()
    {
        try
        {
            if (_removeRetroAchievementsApiKeyOnSave)
            {
                await _retroAchievementsAccountService
                    .DeleteApiKeyAsync()
                    .ConfigureAwait(true);
            }
            else if (!string.IsNullOrWhiteSpace(RetroAchievementsApiKey))
            {
                await _retroAchievementsAccountService
                    .StoreApiKeyAsync(RetroAchievementsApiKey)
                    .ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            RetroAchievementsStatusText = T(
                "Settings_RetroAchievementsStatusSecretWriteFailed",
                "The Web API key could not be stored. The settings were not saved.");
            return false;
        }

        var settings = _appSettings.RetroAchievements ??= new Models.RetroAchievementsSettings();
        var username = string.IsNullOrWhiteSpace(RetroAchievementsUsername)
            ? null
            : RetroAchievementsUsername.Trim();

        settings.Enabled = RetroAchievementsEnabled;
        settings.ShowInBigMode = RetroAchievementsShowInBigMode;
        settings.Username = username;

        if (!string.IsNullOrWhiteSpace(_verifiedRetroAchievementsUsername) &&
            string.Equals(
                username,
                _verifiedRetroAchievementsUsername,
                StringComparison.OrdinalIgnoreCase))
        {
            settings.UserUlid = _verifiedRetroAchievementsUserUlid;
        }
        else if (!string.Equals(
                     username,
                     _targetSettings.RetroAchievements?.Username,
                     StringComparison.OrdinalIgnoreCase))
        {
            settings.UserUlid = null;
        }

        return true;
    }

    partial void OnRetroAchievementsApiKeyChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            _removeRetroAchievementsApiKeyOnSave = false;
    }
}
