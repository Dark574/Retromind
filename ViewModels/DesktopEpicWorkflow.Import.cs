using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Models.Stores;
using Retromind.Views;

namespace Retromind.ViewModels;

internal sealed partial class DesktopEpicWorkflow
{
    private const string EpicDisplayName = "Epic Games";

    internal async Task ImportEpicAsync(MediaNode? targetNode)
    {
        if (targetNode == null || CurrentWindow is not { } owner) return;

        if (!await EnsureEpicSignInAsync(owner))
            return;

        IReadOnlyList<StoreGameRecord> ownedGames;
        try
        {
            using (BeginBusyCursor(owner))
            {
                await Task.Yield();
                ownedGames = await _epicStoreLibraryProvider.GetOwnedGamesAsync();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Epic] Failed to load owned games: {ex.Message}");
            await ShowInfoDialog(
                owner,
                T("Epic.LibraryLoadFailed", "The Epic Games library could not be loaded."));
            return;
        }

        if (ownedGames.Count == 0)
        {
            await ShowInfoDialog(owner, T("Epic.LibraryEmpty", "No Epic Games titles were found."));
            return;
        }

        var existingStoreGameIds = targetNode.Items
            .Select(item => StoreGameIdentityHelper.TryGetIdentity(
                item,
                out var providerId,
                out var gameId) &&
                string.Equals(providerId, StoreProviderIds.Epic, StringComparison.OrdinalIgnoreCase)
                    ? gameId
                    : null)
            .Where(static gameId => !string.IsNullOrWhiteSpace(gameId))
            .Select(static gameId => gameId!)
            .ToHashSet(StringComparer.Ordinal);

        var pickerVm = new GogPickerDialogViewModel(
            ownedGames,
            existingStoreGameIds,
            BuildStoreUsageCountByGameId(StoreProviderIds.Epic),
            providerId: StoreProviderIds.Epic,
            providerDisplayName: EpicDisplayName);
        var pickerDialog = new GogPickerDialogView { DataContext = pickerVm };

        IReadOnlyList<StoreGameRecord> selectedGames;
        try
        {
            var accepted = false;
            pickerVm.RequestClose += result =>
            {
                accepted = result;
                pickerDialog.Close(result);
            };

            await pickerDialog.ShowDialog<bool>(owner);
            if (!accepted)
                return;

            selectedGames = pickerVm.GetSelectedGames();
        }
        finally
        {
            pickerVm.Dispose();
        }

        var itemsToAdd = selectedGames
            .Where(game => string.Equals(
                game.ProviderId,
                StoreProviderIds.Epic,
                StringComparison.OrdinalIgnoreCase))
            .Where(game => !string.IsNullOrWhiteSpace(game.StoreGameId))
            .Where(game => existingStoreGameIds.Add(game.StoreGameId))
            .OrderBy(game => game.Title, StringComparer.OrdinalIgnoreCase)
            .Select(game => new MediaItem
            {
                Title = string.IsNullOrWhiteSpace(game.Title)
                    ? $"{EpicDisplayName} {game.StoreGameId}"
                    : game.Title,
                MediaType = MediaType.Command,
                Source = EpicDisplayName,
                Platform = game.Platform,
                CustomFields = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [CustomFieldKeyHelper.StoreProviderId] = StoreProviderIds.Epic,
                    [CustomFieldKeyHelper.StoreGameId] = game.StoreGameId
                }
            })
            .ToList();

        if (itemsToAdd.Count == 0)
        {
            await ShowInfoDialog(
                owner,
                T("Epic.Picker.NoNewForNode", "The selection did not contain new Epic Games titles for this category."));
            return;
        }

        await Workspace.PublishImportAsync(targetNode, itemsToAdd);
    }

    private async Task<bool> EnsureEpicSignInAsync(Window owner)
    {
        try
        {
            using (BeginBusyCursor(owner))
            {
                await Task.Yield();
                if (await _epicStoreAuthProvider.TryRefreshSessionAsync())
                    return true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Epic] Session validation failed: {ex.Message}");
            await ShowInfoDialog(
                owner,
                T("Epic.Auth.CheckFailed", "The Epic Games sign-in state could not be verified."));
            return false;
        }

        var signIn = await ShowConfirmDialog(
            owner,
            T("Epic.Auth.RequiredPrompt", "Epic Games sign-in is required. Open secure sign-in in your browser now?"));
        if (!signIn)
            return false;

        try
        {
            await _epicStoreAuthProvider.SignInInteractiveAsync(
                (challenge, signInCt) => ResolveEpicSignInChallengeAsync(owner, challenge, signInCt));
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Epic] Interactive sign-in failed: {ex.Message}");
            await ShowInfoDialog(
                owner,
                T("Epic.Auth.SignInFailed", "Epic Games sign-in failed. Request a new authorization code and try again."));
            return false;
        }
    }

    private static async Task<StoreSignInResponse?> ResolveEpicSignInChallengeAsync(
        Window owner,
        StoreSignInChallenge challenge,
        CancellationToken ct)
    {
        if (challenge.ResponseKind != StoreSignInResponseKind.AuthorizationCode)
            throw new InvalidOperationException("Epic sign-in requires an authorization code.");

        if (!SystemBrowserLauncher.TryOpen(challenge.AuthorizationUri, out var browserError))
            throw new InvalidOperationException("Could not open the system browser for Epic sign-in.", browserError);

        var dialog = new EpicAuthorizationCodeView(challenge.AuthorizationUri);
        var authorizationCode = await dialog.CaptureAsync(owner, ct);
        return string.IsNullOrWhiteSpace(authorizationCode)
            ? null
            : StoreSignInResponse.FromAuthorizationCode(authorizationCode);
    }
}
