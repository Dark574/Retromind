using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Avalonia.Controls;
using Avalonia.Platform;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Models.Stores;
using Retromind.Views;

namespace Retromind.ViewModels;

internal sealed partial class DesktopGogWorkflow
{
    private const string GogProviderId = StoreProviderIds.Gog;
    private const string GogDisplayName = "GOG";
    private const string GogForceBrowserLoginEnvironmentVariable = "RETROMIND_GOG_FORCE_BROWSER_LOGIN";
    private static readonly Uri GogDefaultWebAuthRedirectUri = new("https://embed.gog.com/on_login_success?origin=client");

    private static bool IsStoreCustomFieldKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        return string.Equals(key, CustomFieldKeyHelper.StoreProviderId, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, CustomFieldKeyHelper.StoreGameId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPlaceholderGogDeveloper(string? developer)
        => string.Equals(developer?.Trim(), GogDisplayName, StringComparison.OrdinalIgnoreCase);

    private static int ScoreGogMetadataTemplateCandidate(MediaItem item)
    {
        var score = 0;

        if (!string.IsNullOrWhiteSpace(item.Description))
            score += 5;
        if (!string.IsNullOrWhiteSpace(item.Developer) && !IsPlaceholderGogDeveloper(item.Developer))
            score += 4;
        if (!string.IsNullOrWhiteSpace(item.Publisher))
            score += 3;
        if (!string.IsNullOrWhiteSpace(item.Platform))
            score += 2;
        if (!string.IsNullOrWhiteSpace(item.Genre))
            score += 2;
        if (!string.IsNullOrWhiteSpace(item.Series))
            score += 1;
        if (!string.IsNullOrWhiteSpace(item.PlayMode))
            score += 1;
        if (!string.IsNullOrWhiteSpace(item.MaxPlayers))
            score += 1;
        if (item.ReleaseDate.HasValue)
            score += 2;
        if (item.Rating > 0)
            score += 1;

        if (item.Assets is { Count: > 0 })
            score += item.Assets.Count(a => !string.IsNullOrWhiteSpace(a.RelativePath)) * 4;

        if (item.CustomFields is { Count: > 0 })
        {
            score += item.CustomFields.Count(kv =>
                !string.IsNullOrWhiteSpace(kv.Key) &&
                !string.IsNullOrWhiteSpace(kv.Value) &&
                !IsStoreCustomFieldKey(kv.Key));
        }

        return score;
    }

    private Dictionary<string, MediaItem> BuildGogMetadataTemplateByGameId()
    {
        var templatesByGameId = new Dictionary<string, MediaItem>(StringComparer.Ordinal);
        foreach (var root in RootItems)
            CollectGogMetadataTemplateRecursive(root, templatesByGameId);

        return templatesByGameId;
    }

    private void CollectGogMetadataTemplateRecursive(MediaNode node, IDictionary<string, MediaItem> templatesByGameId)
    {
        foreach (var item in node.Items)
        {
            var gameId = GogMediaItemStateHelper.TryGetGameId(item);
            if (string.IsNullOrWhiteSpace(gameId))
                continue;

            if (!templatesByGameId.TryGetValue(gameId, out var existingTemplate))
            {
                templatesByGameId[gameId] = item;
                continue;
            }

            var existingScore = ScoreGogMetadataTemplateCandidate(existingTemplate);
            var currentScore = ScoreGogMetadataTemplateCandidate(item);
            if (currentScore > existingScore)
                templatesByGameId[gameId] = item;
        }

        foreach (var child in node.Children)
            CollectGogMetadataTemplateRecursive(child, templatesByGameId);
    }

    private void ApplyGogMetadataTemplate(MediaItem target, MediaItem template)
    {
        target.Description = template.Description;

        if (!string.IsNullOrWhiteSpace(template.Developer) && !IsPlaceholderGogDeveloper(template.Developer))
            target.Developer = template.Developer;

        target.Publisher = template.Publisher;
        if (string.IsNullOrWhiteSpace(target.Platform))
            target.Platform = template.Platform;

        target.Genre = template.Genre;
        target.Series = template.Series;
        target.ReleaseType = template.ReleaseType;
        target.SortTitle = template.SortTitle;
        target.PlayMode = template.PlayMode;
        target.MaxPlayers = template.MaxPlayers;
        target.ReleaseDate = template.ReleaseDate;
        target.Rating = template.Rating;

        if (template.Tags is { Count: > 0 })
        {
            target.Tags = new ObservableCollection<string>(
                template.Tags.Where(t => !string.IsNullOrWhiteSpace(t)));
        }

        if (template.CustomFields is { Count: > 0 })
        {
            foreach (var kv in template.CustomFields)
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value))
                    continue;
                if (IsStoreCustomFieldKey(kv.Key))
                    continue;

                target.CustomFields[kv.Key] = kv.Value;
            }
        }

        if (template.Assets is { Count: > 0 })
        {
            var existingAssets = new HashSet<(AssetType Type, string Path)>();
            foreach (var asset in target.Assets)
            {
                if (string.IsNullOrWhiteSpace(asset.RelativePath))
                    continue;

                existingAssets.Add((asset.Type, asset.RelativePath));
            }

            foreach (var asset in template.Assets)
            {
                if (string.IsNullOrWhiteSpace(asset.RelativePath))
                    continue;

                var key = (asset.Type, asset.RelativePath);
                if (!existingAssets.Add(key))
                    continue;

                target.Assets.Add(new MediaAsset
                {
                    Type = asset.Type,
                    RelativePath = asset.RelativePath
                });
            }
        }
    }

    internal async Task ImportGogAsync(MediaNode? targetNode)
    {
        if (targetNode == null || CurrentWindow is not { } owner) return;

        StoreAuthState authState;
        try
        {
            authState = await _gogStoreAuthProvider.GetAuthStateAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GOG] Failed to query auth state: {ex.Message}");
            await ShowConfirmDialog(owner, T("Gog.AuthCheckFailed", "GOG sign-in state could not be verified."));
            return;
        }

        if (!authState.IsAuthenticated)
        {
            try
            {
                var refreshed = await _gogStoreAuthProvider.TryRefreshSessionAsync();
                if (refreshed)
                    authState = await _gogStoreAuthProvider.GetAuthStateAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GOG] Silent session refresh failed: {ex.Message}");
            }
        }

        if (!authState.IsAuthenticated)
        {
            var signIn = await ShowConfirmDialog(owner,
                T("Gog.SignInRequiredPrompt", "GOG sign-in is required. Open secure sign-in now?"));
            if (!signIn)
                return;

            try
            {
                await _gogStoreAuthProvider.SignInInteractiveAsync(
                    (challenge, signInCt) => ResolveGogSignInChallengeAsync(owner, challenge, signInCt));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (TimeoutException ex)
            {
                Debug.WriteLine($"[GOG] Interactive sign-in timed out: {ex.Message}");
                await ShowConfirmDialog(owner, T("Gog.SignInTimeout", "GOG sign-in timed out. Please retry."));
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GOG] Interactive sign-in failed: {ex.Message}");
                await ShowConfirmDialog(
                    owner,
                    GetGogSignInErrorMessage(ex));
                return;
            }
        }

        IReadOnlyList<StoreGameRecord> ownedGames;
        IReadOnlyDictionary<string, int> usageByGameId = new Dictionary<string, int>(StringComparer.Ordinal);
        Dictionary<string, MediaItem> metadataTemplateByGameId;
        try
        {
            using (BeginBusyCursor(owner))
            {
                // Let cursor state update before potentially long store calls.
                await Task.Yield();
                ownedGames = await _gogStoreLibraryProvider.GetOwnedGamesAsync();
                metadataTemplateByGameId = BuildGogMetadataTemplateByGameId();
                usageByGameId = BuildGogUsageCountByGameId();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GOG] Failed to prepare library import: {ex.Message}");
            await ShowConfirmDialog(owner, T("Gog.LibraryLoadFailed", "GOG library could not be loaded."));
            return;
        }

        if (ownedGames.Count == 0)
        {
            await ShowConfirmDialog(owner, T("Gog.LibraryEmpty", "No GOG games found."));
            return;
        }

        var existingStoreGameIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in targetNode.Items)
        {
            var existingId = GogMediaItemStateHelper.TryGetGameId(item);
            if (!string.IsNullOrWhiteSpace(existingId))
                existingStoreGameIds.Add(existingId);
        }

        var pickerVm = new GogPickerDialogViewModel(ownedGames, existingStoreGameIds, usageByGameId);
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

        if (selectedGames.Count == 0)
            return;

        var itemsToAdd = new List<MediaItem>();
        using (BeginBusyCursor(owner))
        {
            await Task.Yield();
            foreach (var game in selectedGames.OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase))
            {
                if (!string.Equals(game.ProviderId, StoreProviderIds.Gog, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.IsNullOrWhiteSpace(game.StoreGameId))
                    continue;

                if (!existingStoreGameIds.Add(game.StoreGameId))
                    continue;

                var title = string.IsNullOrWhiteSpace(game.Title)
                    ? string.Format(T("Gog.GameFallbackTitleFormat", "GOG {0}"), game.StoreGameId)
                    : game.Title;

                var newItem = new MediaItem
                {
                    Title = title,
                    MediaType = MediaType.Command,
                    Source = GogDisplayName,
                    Platform = game.Platform,
                    CustomFields = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [CustomFieldKeyHelper.StoreProviderId] = GogProviderId,
                        [CustomFieldKeyHelper.StoreGameId] = game.StoreGameId
                    }
                };

                if (metadataTemplateByGameId.TryGetValue(game.StoreGameId, out var template))
                    ApplyGogMetadataTemplate(newItem, template);

                itemsToAdd.Add(newItem);
            }
        }

        if (itemsToAdd.Count == 0)
        {
            await ShowConfirmDialog(
                owner,
                T("Gog.Picker.NoNewForNode", "Selection did not contain new GOG games for this category."));
            return;
        }

        await Workspace.PublishImportAsync(targetNode, itemsToAdd);
    }

    private static string GetGogSignInErrorMessage(Exception exception)
    {
        var message = exception.Message;
        var inAppUnavailable = exception is PlatformNotSupportedException;

        if (inAppUnavailable)
            return T("Gog.InAppAuthUnavailable", "Embedded web authentication is not available on this platform.");

        if (message.Contains("redirect_uri_mismatch", StringComparison.OrdinalIgnoreCase))
            return T("Gog.RedirectMismatch", "GOG rejected the OAuth redirect URI. Please update OAuth client settings or use a compatible client.");

        if (message.Contains("authorization code", StringComparison.OrdinalIgnoreCase))
            return T("Gog.CallbackMissingCode", "The authentication callback did not include an authorization code.");

        if (message.Contains("authorization URL", StringComparison.OrdinalIgnoreCase))
            return T("Gog.InvalidAuthorizeUri", "The received GOG authorization URL is invalid.");

        if (message.Contains("redirect URI", StringComparison.OrdinalIgnoreCase))
            return T("Gog.InvalidRedirectUri", "The configured OAuth redirect URI is invalid.");

        return T("Gog.SignInFailed", "GOG sign-in failed.");
    }

    private async Task<Uri?> CaptureGogCallbackUriInAppAsync(Window owner, Uri authorizeUri, System.Threading.CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsValidGogAuthorizeUri(authorizeUri))
            throw new InvalidOperationException("Invalid GOG authorization URL.");

        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Embedded GOG authentication is only supported on Linux.");

        var redirectUri = ResolveRedirectUriFromAuthorizeUri(authorizeUri);
        if (!redirectUri.IsAbsoluteUri || !string.Equals(redirectUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Invalid OAuth redirect URI.");
        }

        if (IsGogBrowserLoginForced() || !HasLinuxWpeRuntime())
            return await CaptureGogCallbackUriViaSystemBrowserAsync(owner, authorizeUri, ct);

        GogAuthenticationView dialog;
        try
        {
            dialog = new GogAuthenticationView(
                authorizeUri,
                redirectUri,
                T("Gog.EmbeddedLoginTitle", "GOG sign-in"),
                T("Gog.EmbeddedLoginHint", "Sign in to GOG in the embedded browser."));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GOG] Could not prepare WPE OAuth dialog ({ex.GetType().Name}); falling back to system browser callback capture.");
            return await CaptureGogCallbackUriViaSystemBrowserAsync(owner, authorizeUri, ct);
        }

        bool accepted;
        try
        {
            accepted = await dialog.AuthenticateAsync(owner, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GOG] WPE OAuth dialog failed ({ex.GetType().Name}); falling back to system browser callback capture.");
            return await CaptureGogCallbackUriViaSystemBrowserAsync(owner, authorizeUri, ct);
        }

        ct.ThrowIfCancellationRequested();
        if (dialog.InitializationException is { } initializationException)
        {
            Debug.WriteLine($"[GOG] WPE OAuth initialization failed ({initializationException.GetType().Name}); falling back to system browser callback capture.");
            return await CaptureGogCallbackUriViaSystemBrowserAsync(owner, authorizeUri, ct);
        }

        return accepted ? dialog.CallbackUri : null;
    }

    private static bool HasLinuxWpeRuntime()
    {
        try
        {
            var adapter = WebViewAdapterInfo.GetAdapterInfo(WebViewAdapterType.WpeWebKit);
            if (adapter.IsInstalled &&
                adapter.SupportedScenarios.HasFlag(WebViewEmbeddingScenario.OffscreenRenderer))
            {
                return true;
            }

            Debug.WriteLine($"[GOG] WPE OAuth unavailable: {adapter.UnavailableReason ?? "unsupported embedding scenario"}.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GOG] WPE availability check failed: {ex.Message}");
        }

        return false;
    }

    internal static bool IsGogBrowserLoginForced()
    {
        var value = Environment.GetEnvironmentVariable(GogForceBrowserLoginEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<Uri?> CaptureGogCallbackUriViaSystemBrowserAsync(
        Window owner,
        Uri authorizeUri,
        System.Threading.CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!SystemBrowserLauncher.TryOpen(authorizeUri, out var browserError))
            throw new InvalidOperationException("Could not open system browser for GOG login.", browserError);

        var redirectUri = ResolveRedirectUriFromAuthorizeUri(authorizeUri);
        var dialog = new GogBrowserCallbackView(authorizeUri, redirectUri);
        return await dialog.CaptureAsync(owner, ct);
    }

    private async Task<StoreSignInResponse?> ResolveGogSignInChallengeAsync(
        Window owner,
        StoreSignInChallenge challenge,
        CancellationToken ct)
    {
        if (challenge.ResponseKind != StoreSignInResponseKind.CallbackUri)
            throw new InvalidOperationException("GOG sign-in requires an OAuth callback URI.");

        var callbackUri = await CaptureGogCallbackUriInAppAsync(
            owner,
            challenge.AuthorizationUri,
            ct);
        return callbackUri == null
            ? null
            : StoreSignInResponse.FromCallbackUri(callbackUri);
    }

    private static Uri ResolveRedirectUriFromAuthorizeUri(Uri authorizeUri)
    {
        var queryValues = HttpUtility.ParseQueryString(authorizeUri.Query);
        var redirectUriRaw = queryValues["redirect_uri"];

        if (!string.IsNullOrWhiteSpace(redirectUriRaw) &&
            Uri.TryCreate(redirectUriRaw, UriKind.Absolute, out var parsedRedirectUri))
        {
            return parsedRedirectUri;
        }

        return GogDefaultWebAuthRedirectUri;
    }

    private static bool IsValidGogAuthorizeUri(Uri authorizeUri)
    {
        return authorizeUri.IsAbsoluteUri &&
               string.Equals(authorizeUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(authorizeUri.Host, "auth.gog.com", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(authorizeUri.AbsolutePath, "/auth", StringComparison.OrdinalIgnoreCase);
    }

    private Dictionary<string, int> BuildGogUsageCountByGameId()
        => BuildStoreUsageCountByGameId(StoreProviderIds.Gog);
}
