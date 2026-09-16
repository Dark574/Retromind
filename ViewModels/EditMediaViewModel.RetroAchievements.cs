using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Models;
using Retromind.Services.GameSystems;
using Retromind.Services.RetroAchievements;

namespace Retromind.ViewModels;

public partial class EditMediaViewModel
{
    private readonly IRetroAchievementsGameIdentificationService _retroAchievementsGameIdentificationService;
    private RetroAchievementsGameIdentity? _stagedRetroAchievementsGame;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(IdentifyRetroAchievementsCommand))]
    private bool _isRetroAchievementsIdentificationBusy;

    [ObservableProperty]
    private string _retroAchievementsIdentificationStatus = string.Empty;

    public bool IsRetroAchievementsIdentificationVisible =>
        _settings.RetroAchievements?.Enabled == true || _stagedRetroAchievementsGame != null;

    public string RetroAchievementsIdentificationTitle =>
        T("RetroAchievements_IdentificationTitle", "RetroAchievements identification");

    public string IdentifyRetroAchievementsText =>
        T("RetroAchievements_Identify", "Identify game");

    public IAsyncRelayCommand IdentifyRetroAchievementsCommand { get; }

    private void InitializeRetroAchievementsIdentification()
    {
        _stagedRetroAchievementsGame = CloneRetroAchievementsGameIdentity(
            _originalItem.RetroAchievementsGame);
        RefreshRetroAchievementsIdentificationStatus();
    }

    private bool CanIdentifyRetroAchievements()
    {
        return !IsRetroAchievementsIdentificationBusy &&
               _settings.RetroAchievements?.Enabled == true &&
               ResolveSelectedGameSystemId() != null &&
               !string.IsNullOrWhiteSpace(GetEditedPrimaryLaunchPath());
    }

    private async Task IdentifyRetroAchievementsAsync()
    {
        if (!CanIdentifyRetroAchievements())
            return;

        var gameSystemId = ResolveSelectedGameSystemId();
        var filePath = GetEditedPrimaryLaunchPath();
        if (gameSystemId == null || string.IsNullOrWhiteSpace(filePath))
            return;

        IsRetroAchievementsIdentificationBusy = true;
        RetroAchievementsIdentificationStatus = T(
            "RetroAchievements_IdentificationRunning",
            "Calculating the game hash and checking RetroAchievements...");

        try
        {
            var result = await _retroAchievementsGameIdentificationService
                .IdentifyAsync(gameSystemId, filePath)
                .ConfigureAwait(true);

            if (result.Game == null)
            {
                _stagedRetroAchievementsGame = null;
                RetroAchievementsIdentificationStatus = T(
                    "RetroAchievements_IdentificationNoMatch",
                    "No matching RetroAchievements game was found for this file.");
            }
            else
            {
                _stagedRetroAchievementsGame = new RetroAchievementsGameIdentity
                {
                    GameId = result.Game.GameId,
                    ConsoleId = result.GameHash.ConsoleId,
                    GameSystemId = result.GameHash.GameSystemId,
                    Hash = result.GameHash.Hash,
                    Title = result.Game.Title
                };
                RetroAchievementsIdentificationStatus = string.Format(
                    T(
                        "RetroAchievements_IdentificationSuccessFormat",
                        "Identified as {0} (game ID {1}). Save the item to keep this assignment."),
                    result.Game.Title,
                    result.Game.GameId);
            }

            OnPropertyChanged(nameof(IsRetroAchievementsIdentificationVisible));
        }
        catch (OperationCanceledException)
        {
            RetroAchievementsIdentificationStatus = T(
                "RetroAchievements_IdentificationCanceled",
                "RetroAchievements identification was canceled.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or
                                   NotSupportedException or
                                   FileNotFoundException or
                                   RetroAchievementsHashException or
                                   RetroAchievementsApiException)
        {
            RetroAchievementsIdentificationStatus = string.Format(
                T(
                    "RetroAchievements_IdentificationFailedFormat",
                    "RetroAchievements identification failed: {0}"),
                ex.Message);
        }
        catch
        {
            RetroAchievementsIdentificationStatus = T(
                "RetroAchievements_IdentificationUnexpectedFailure",
                "RetroAchievements identification failed unexpectedly.");
        }
        finally
        {
            IsRetroAchievementsIdentificationBusy = false;
        }
    }

    private string? ResolveSelectedGameSystemId()
    {
        return GameSystemCatalog.NormalizeId(SelectedGameSystem?.Id) ??
               GameSystemResolver.ResolveForNode(_parentNode, _rootNodes);
    }

    private void InvalidateRetroAchievementsIdentification()
    {
        _stagedRetroAchievementsGame = null;
        RefreshRetroAchievementsIdentificationStatus();
    }

    private void RefreshRetroAchievementsIdentificationStatus()
    {
        if (_stagedRetroAchievementsGame != null)
        {
            RetroAchievementsIdentificationStatus = string.Format(
                T(
                    "RetroAchievements_IdentificationExistingFormat",
                    "Identified as {0} (game ID {1})."),
                _stagedRetroAchievementsGame.Title,
                _stagedRetroAchievementsGame.GameId);
        }
        else if (_settings.RetroAchievements?.Enabled != true)
        {
            RetroAchievementsIdentificationStatus = T(
                "RetroAchievements_IdentificationDisabled",
                "Enable RetroAchievements in the application settings to identify this game.");
        }
        else if (ResolveSelectedGameSystemId() == null)
        {
            RetroAchievementsIdentificationStatus = T(
                "RetroAchievements_IdentificationMissingSystem",
                "Select or inherit a game system before identifying this game.");
        }
        else if (string.IsNullOrWhiteSpace(GetEditedPrimaryLaunchPath()))
        {
            RetroAchievementsIdentificationStatus = T(
                "RetroAchievements_IdentificationMissingFile",
                "Select a launch file before identifying this game.");
        }
        else
        {
            RetroAchievementsIdentificationStatus = T(
                "RetroAchievements_IdentificationNotIdentified",
                "This game has not been identified yet.");
        }

        IdentifyRetroAchievementsCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsRetroAchievementsIdentificationVisible));
    }

    partial void OnSelectedGameSystemChanged(
        GameSystemSelectionOption? oldValue,
        GameSystemSelectionOption? newValue)
    {
        if (oldValue == null)
        {
            IdentifyRetroAchievementsCommand?.NotifyCanExecuteChanged();
            return;
        }

        if (!string.Equals(oldValue.Id, newValue?.Id, StringComparison.Ordinal))
            InvalidateRetroAchievementsIdentification();
    }

    private static RetroAchievementsGameIdentity? CloneRetroAchievementsGameIdentity(
        RetroAchievementsGameIdentity? identity)
    {
        if (identity == null)
            return null;

        return new RetroAchievementsGameIdentity
        {
            GameId = identity.GameId,
            ConsoleId = identity.ConsoleId,
            GameSystemId = identity.GameSystemId,
            Hash = identity.Hash,
            Title = identity.Title
        };
    }
}
