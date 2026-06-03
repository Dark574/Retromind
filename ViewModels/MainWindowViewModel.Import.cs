using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Web;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Models.Stores;
using Retromind.Resources;
using Retromind.Views;

namespace Retromind.ViewModels;

public partial class MainWindowViewModel
{
    // Regex to detect Disk/Disc/Side/Part suffixes
    // Matches: " (Disk 1)", "_Disk1", " (Side A)", " - CD 1", etc.
    private static readonly System.Text.RegularExpressions.Regex MultiDiscRegex =
        // Require a clear separator (start/space/_/-/bracket) before Disc/Side tokens to avoid
        // matching inside words like "Unterirdische" (contains "disc").
        new(@"(?:^|[\s_\-]|\(|\[)\s*(?<kind>Disk|Disc|CD|Side|Part)\s*(?<token>[0-9A-H]+)(?:\s*(?:\)|\]))?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private const string GogProviderId = "gog";
    private const string GogDisplayName = "GOG";
    private const string StoreProviderIdField = "Store.ProviderId";
    private const string StoreGameIdField = "Store.GameId";
    private static readonly Uri GogDefaultWebAuthRedirectUri = new("https://embed.gog.com/on_login_success?origin=client");
    private const string LinuxWebKitGtkLibraryName = "libwebkit2gtk";
    private const string LinuxWebKitGtkAliasFileName = "libwebkit2gtk.so";
    private static readonly string[] LinuxWebKitGtkLibraryCandidates =
    [
        LinuxWebKitGtkLibraryName,
        LinuxWebKitGtkAliasFileName,
        "libwebkit2gtk-4.1.so.0",
        "libwebkit2gtk-4.1.so",
        "libwebkit2gtk-4.0.so.37",
        "libwebkit2gtk-4.0.so"
    ];

    private static int? ParseDiscIndex(string token)
    {
        // Supports: "1", "2", ... and "A".."H" (Side A/B, etc.)
        if (int.TryParse(token, out var n) && n > 0)
            return n;

        if (token.Length == 1)
        {
            var c = char.ToUpperInvariant(token[0]);
            if (c is >= 'A' and <= 'H')
                return (c - 'A') + 1;
        }

        return null;
    }

    private static string? BuildDiscLabel(string kind, string token, int? index)
    {
        // Keep labels user-friendly and stable for UI and playlist readability.
        kind = kind.Trim();

        if (string.Equals(kind, "Side", StringComparison.OrdinalIgnoreCase) && token.Length == 1)
        {
            var side = char.ToUpperInvariant(token[0]);
            if (side is >= 'A' and <= 'H')
                return $"Side {side}";
        }

        if (!string.IsNullOrWhiteSpace(token))
            return $"{kind} {token}";

        if (index.HasValue)
            return $"{kind} {index.Value}";

        return null;
    }

    private static (int? Index, string? Label) TryGetDiscInfoFromFileName(string fileNameWithoutExtension)
    {
        var match = MultiDiscRegex.Match(fileNameWithoutExtension);
        if (!match.Success)
            return (null, null);

        var kind = match.Groups["kind"].Value.Trim();
        var token = match.Groups["token"].Value.Trim();

        var idx = ParseDiscIndex(token);
        var label = BuildDiscLabel(kind, token, idx);

        return (idx, label);
    }

    private static string? TryGetStoreGameId(MediaItem item)
    {
        if (!item.CustomFields.TryGetValue(StoreProviderIdField, out var providerId) ||
            !IsGogProvider(providerId))
        {
            return null;
        }

        return item.CustomFields.TryGetValue(StoreGameIdField, out var gameId)
            ? gameId
            : null;
    }

    private static bool IsGogProvider(string? providerId)
        => string.Equals(providerId, GogProviderId, StringComparison.OrdinalIgnoreCase);

    private static bool IsGogNode(MediaNode node)
        => IsGogProvider(node.StoreProviderId);

    private static bool IsStoreCustomFieldKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        return string.Equals(key, StoreProviderIdField, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, StoreGameIdField, StringComparison.OrdinalIgnoreCase);
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
            var gameId = TryGetStoreGameId(item);
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

    private static IDisposable BeginBusyCursor(Window owner)
        => new BusyCursorScope(owner);

    private sealed class BusyCursorScope : IDisposable
    {
        private readonly Window _owner;
        private readonly Cursor? _previousCursor;
        private bool _disposed;

        public BusyCursorScope(Window owner)
        {
            _owner = owner;
            _previousCursor = owner.Cursor;
            _owner.Cursor = new Cursor(StandardCursorType.Wait);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _owner.Cursor = _previousCursor;
        }
    }
    
    // --- Import Actions ---

    private async Task AddGogMediaAsync(MediaNode? targetNode)
    {
        if (targetNode == null) targetNode = SelectedNode;
        if (targetNode == null || CurrentWindow is not { } owner) return;

        var isGogNode = IsGogNode(targetNode);

        StoreAuthState authState;
        try
        {
            authState = await _storeAuthProvider.GetAuthStateAsync();
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
                var refreshed = await _storeAuthProvider.TryRefreshSessionAsync();
                if (refreshed)
                    authState = await _storeAuthProvider.GetAuthStateAsync();
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
                await _storeAuthProvider.SignInInteractiveAsync(
                    (authorizeUri, signInCt) => CaptureGogCallbackUriInAppAsync(owner, authorizeUri, signInCt));
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
                var inAppUnavailable = ex is PlatformNotSupportedException;
                var waylandAppImageUnsupported = inAppUnavailable &&
                                                 ex.Message.IndexOf("AppImage Wayland", StringComparison.OrdinalIgnoreCase) >= 0;
                var mismatch = ex.Message.IndexOf("redirect_uri_mismatch", StringComparison.OrdinalIgnoreCase) >= 0;
                var missingCode = ex.Message.IndexOf("authorization code", StringComparison.OrdinalIgnoreCase) >= 0;
                var invalidAuthorizeUri = ex.Message.IndexOf("authorization URL", StringComparison.OrdinalIgnoreCase) >= 0;
                var invalidRedirectUri = ex.Message.IndexOf("redirect URI", StringComparison.OrdinalIgnoreCase) >= 0;
                var signInErrorMessage = waylandAppImageUnsupported
                    ? T(
                        "Gog.InAppAuthUnavailableWaylandAppImage",
                        "Embedded web authentication is currently not supported in AppImage Wayland sessions. Restart Retromind with --avalonia-platform=x11 and retry.")
                    : inAppUnavailable
                    ? T("Gog.InAppAuthUnavailable", "Embedded web authentication is not available on this platform.")
                    : mismatch
                        ? T("Gog.RedirectMismatch", "GOG rejected the OAuth redirect URI. Please update OAuth client settings or use a compatible client.")
                    : missingCode
                        ? T("Gog.CallbackMissingCode", "The authentication callback did not include an authorization code.")
                    : invalidAuthorizeUri
                        ? T("Gog.InvalidAuthorizeUri", "The received GOG authorization URL is invalid.")
                    : invalidRedirectUri
                        ? T("Gog.InvalidRedirectUri", "The configured OAuth redirect URI is invalid.")
                        : T("Gog.SignInFailed", "GOG sign-in failed.");
                await ShowConfirmDialog(
                    owner,
                    signInErrorMessage);
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
                ownedGames = await _storeLibraryProvider.GetOwnedGamesAsync();
                metadataTemplateByGameId = BuildGogMetadataTemplateByGameId();
                if (!isGogNode)
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
            var existingId = TryGetStoreGameId(item);
            if (!string.IsNullOrWhiteSpace(existingId))
                existingStoreGameIds.Add(existingId);
        }

        IReadOnlyList<StoreGameRecord> selectedGames;

        if (isGogNode)
        {
            selectedGames = ownedGames;
        }
        else
        {
            var pickerVm = new GogPickerDialogViewModel(ownedGames, existingStoreGameIds, usageByGameId);
            var pickerDialog = new GogPickerDialogView { DataContext = pickerVm };

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
        }

        if (selectedGames.Count == 0)
            return;

        var itemsToAdd = new List<MediaItem>();
        using (BeginBusyCursor(owner))
        {
            await Task.Yield();
            foreach (var game in selectedGames.OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase))
            {
                if (!string.Equals(game.ProviderId, "gog", StringComparison.OrdinalIgnoreCase))
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
                        [StoreProviderIdField] = GogProviderId,
                        [StoreGameIdField] = game.StoreGameId
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
                isGogNode
                    ? T("Gog.Node.SyncNoChanges", "GOG node is already up to date.")
                    : T("Gog.Picker.NoNewForNode", "Selection did not contain new GOG games for this node."));
            return;
        }

        ApplyEffectiveParentalProtection(targetNode, itemsToAdd);

        await UiThreadHelper.InvokeAsync(() =>
        {
            InsertMediaItemsOptimized(targetNode.Items, itemsToAdd);

            _libraryTracker.MarkDirty();

            if (IsNodeInCurrentView(targetNode))
                UpdateContent();
        });

        await SaveData();
    }

    private async Task<Uri?> CaptureGogCallbackUriInAppAsync(Window owner, Uri authorizeUri, System.Threading.CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsValidGogAuthorizeUri(authorizeUri))
            throw new InvalidOperationException("Invalid GOG authorization URL.");

        if (OperatingSystem.IsLinux())
        {
            if (IsLinuxWaylandAppImageSession())
            {
                return await CaptureGogCallbackUriViaSystemBrowserAsync(owner, authorizeUri, ct);
            }

            EnsureLinuxWebKitGtkAlias();
            if (!HasLinuxWebKitGtkRuntime())
            {
                return await CaptureGogCallbackUriViaSystemBrowserAsync(owner, authorizeUri, ct);
            }
        }

        var redirectUri = ResolveRedirectUriFromAuthorizeUri(authorizeUri);
        if (!redirectUri.IsAbsoluteUri || !string.Equals(redirectUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Invalid OAuth redirect URI.");
        }

        var options = new WebAuthenticatorOptions(authorizeUri, redirectUri)
        {
            PreferNativeWebDialog = true,
            NonPersistent = true
        };

        Uri? callbackUri;
        try
        {
            var result = await WebAuthenticationBroker.AuthenticateAsync(owner, options);
            callbackUri = result.CallbackUri;
        }
        catch (Exception ex) when (IsMissingLinuxWebKitGtk(ex))
        {
            Debug.WriteLine($"[GOG] Embedded OAuth unavailable ({ex.GetType().Name}), falling back to system browser callback capture.");
            return await CaptureGogCallbackUriViaSystemBrowserAsync(owner, authorizeUri, ct);
        }

        ct.ThrowIfCancellationRequested();
        return callbackUri;
    }

    private async Task<Uri?> CaptureGogCallbackUriViaSystemBrowserAsync(
        Window owner,
        Uri authorizeUri,
        System.Threading.CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = authorizeUri.ToString(),
                UseShellExecute = true
            });
            if (process == null)
                throw new InvalidOperationException("Browser process could not be started.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Could not open system browser for GOG login.", ex);
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var prompt = string.Format(
                T(
                    "Gog.CallbackPromptFormat",
                    "Complete login in your browser, then paste the final callback URL here.\n\nIf needed, reopen:\n{0}"),
                authorizeUri);

            var input = await PromptForName(owner, prompt);
            if (string.IsNullOrWhiteSpace(input))
                return null;

            var trimmed = input.Trim();
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var callbackUri))
                return callbackUri;

            await ShowInfoDialog(owner, T("Gog.CallbackInvalidUri", "The entered value is not a valid URL."));
        }
    }

    private static bool IsLinuxWaylandAppImageSession()
    {
        if (!OperatingSystem.IsLinux() || !AppImageToolResolver.IsAppImageRuntime())
            return false;

        var explicitPlatform = Environment.GetEnvironmentVariable("AVALONIA_PLATFORM");
        if (string.Equals(explicitPlatform, "wayland", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(explicitPlatform, "x11", StringComparison.OrdinalIgnoreCase))
            return false;

        var sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        if (string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase))
            return true;

        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
    }

    private static bool HasLinuxWebKitGtkRuntime()
    {
        if (!OperatingSystem.IsLinux())
            return true;

        foreach (var candidate in LinuxWebKitGtkLibraryCandidates)
        {
            if (!NativeLibrary.TryLoad(candidate, out var handle))
                continue;

            NativeLibrary.Free(handle);
            return true;
        }

        return false;
    }

    private static void EnsureLinuxWebKitGtkAlias()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var aliasPath = Path.Combine(AppContext.BaseDirectory, LinuxWebKitGtkAliasFileName);
        if (File.Exists(aliasPath))
            return;

        foreach (var candidatePath in EnumerateLinuxWebKitGtkCandidatePaths())
        {
            if (!File.Exists(candidatePath))
                continue;

            try
            {
                File.CreateSymbolicLink(aliasPath, candidatePath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GOG] Could not create local WebKitGTK alias: {ex.Message}");
            }

            return;
        }
    }

    private static IEnumerable<string> EnumerateLinuxWebKitGtkCandidatePaths()
    {
        var directories = new[]
        {
            "/usr/lib",
            "/usr/lib64",
            "/usr/lib/x86_64-linux-gnu",
            "/lib",
            "/lib64",
            "/lib/x86_64-linux-gnu"
        };

        foreach (var directory in directories)
        {
            yield return Path.Combine(directory, "libwebkit2gtk-4.1.so.0");
            yield return Path.Combine(directory, "libwebkit2gtk-4.1.so");
            yield return Path.Combine(directory, "libwebkit2gtk-4.0.so.37");
            yield return Path.Combine(directory, "libwebkit2gtk-4.0.so");
        }
    }

    private static bool IsMissingLinuxWebKitGtk(Exception ex)
    {
        if (!OperatingSystem.IsLinux())
            return false;

        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            if (current.Message.IndexOf("webkit2gtk", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        return false;
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
    {
        var usageByGameId = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var root in RootItems)
            CollectGogUsageRecursive(root, usageByGameId);

        return usageByGameId;
    }

    private void CollectGogUsageRecursive(MediaNode node, IDictionary<string, int> usageByGameId)
    {
        foreach (var item in node.Items)
        {
            var storeGameId = TryGetStoreGameId(item);
            if (string.IsNullOrWhiteSpace(storeGameId))
                continue;

            if (usageByGameId.TryGetValue(storeGameId, out var existingCount))
                usageByGameId[storeGameId] = existingCount + 1;
            else
                usageByGameId[storeGameId] = 1;
        }

        foreach (var child in node.Children)
            CollectGogUsageRecursive(child, usageByGameId);
    }

    private async Task ImportRomsAsync(MediaNode? targetNode)
    {
        // Resolve target node
        if (targetNode == null) targetNode = SelectedNode;
        if (targetNode == null || CurrentWindow is not { } owner) return;
        
        // If the context menu was opened on a node that is ALSO the selected node, use the object reference to be safe
        if (SelectedNode != null && targetNode.Id == SelectedNode.Id && targetNode != SelectedNode) 
            targetNode = SelectedNode;

        var storageProvider = StorageProvider ?? owner.StorageProvider;
        var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Strings.Ctx_ImportRoms, 
            AllowMultiple = false
        });

        if (folders.Count == 0) return;
        var sourcePath = folders[0].Path.LocalPath;

        var defaultExt = "iso,bin,cue,rom,smc,sfc,nes,gb,gba,nds,md,n64,z64,v64,exe,sh";
        var extensionsStr = await PromptForName(owner, Strings.Dialog_FileExtensionsPrompt) ?? defaultExt;
        if (string.IsNullOrWhiteSpace(extensionsStr)) return;

        var extensions = extensionsStr.Split(',', StringSplitOptions.RemoveEmptyEntries);
        
        // Run heavy import logic
        var importedItems = await _importService.ImportFromFolderAsync(sourcePath, extensions);

        if (!importedItems.Any()) return;
        
        // Snapshot node path once (stable, avoids repeated computation)
        var nodePath = PathHelper.GetNodePath(targetNode, RootItems);
        var effectiveDefaultEmulatorId = ResolveEffectiveDefaultEmulatorId(targetNode);
        var defaultMediaType = string.IsNullOrWhiteSpace(effectiveDefaultEmulatorId)
            ? MediaType.Native
            : MediaType.Emulator;

        // 1) Decide what to add (no UI mutations)
        var itemsToAdd = importedItems
            .Where(item =>
            {
                var incoming = item.GetPrimaryLaunchPath();
                if (string.IsNullOrWhiteSpace(incoming))
                    return false;

                return !targetNode.Items.Any(existing =>
                    string.Equals(existing.GetPrimaryLaunchPath(), incoming, StringComparison.OrdinalIgnoreCase));
            })
            .ToList();

        if (itemsToAdd.Count == 0) return;

        if (defaultMediaType == MediaType.Emulator)
        {
            foreach (var item in itemsToAdd)
                item.MediaType = MediaType.Emulator;
        }

        ApplyEffectiveParentalProtection(targetNode, itemsToAdd);

        // 2) Scan assets off the UI thread (filesystem only)
        var scanned = await Task.Run(() =>
        {
            var list = new List<(MediaItem Item, List<MediaAsset> Assets)>(itemsToAdd.Count);

            foreach (MediaItem item in itemsToAdd)
            {
                var assets = _fileService.ScanItemAssets(item, nodePath);
                list.Add((Item: item, Assets: assets));
            }

            return list;
        });

        // 3) Apply everything on UI thread (Items/Assets/Sort)
        await UiThreadHelper.InvokeAsync(() =>
        {
            var newItems = scanned.Select(entry => entry.Item).ToList();
            InsertMediaItemsOptimized(targetNode.Items, newItems);

            foreach (var (item, assets) in scanned)
            {
                item.Assets.Clear();
                foreach (var asset in assets)
                    item.Assets.Add(asset);
            }

            _libraryTracker.MarkDirty();

            if (IsNodeInCurrentView(targetNode))
                UpdateContent();
        });

        // 4) Persist (IO; ok to await from here)
        await SaveData();
    }
    
    private async Task ImportSteamAsync(MediaNode? targetNode)
    {
        if (targetNode == null) targetNode = SelectedNode;
        if (targetNode == null || CurrentWindow is not { } owner) return;

        var discoveredSteamApps = new List<string>();
        var items = await _storeService.ImportSteamGamesAsync(discoveredSteamAppsPaths: discoveredSteamApps);
        if (items.Count == 0)
        {
            var tryManual = await ShowConfirmDialog(owner, Strings.Dialog_NoSteamGamesFound_SelectPath);
            if (!tryManual) return;

            var storageProvider = StorageProvider ?? owner.StorageProvider;
            var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Strings.Dialog_SelectSteamLibraryFolder,
                AllowMultiple = false
            });

            if (folders.Count == 0) return;
            var manualPath = folders[0].Path.LocalPath;
            discoveredSteamApps.Clear();
            items = await _storeService.ImportSteamGamesAsync(manualPath, discoveredSteamApps);

            if (items.Count == 0)
            {
                await ShowConfirmDialog(owner, Strings.Dialog_NoSteamGamesFound);
                return;
            }
        }

        var message = string.Format(Strings.Dialog_ConfirmImportSteamFormat, items.Count, targetNode.Name);
        if (!await ShowConfirmDialog(owner, message))
            return;

        StoreSteamLibraryPaths(discoveredSteamApps);

        // Determine adds off-thread-safe (pure checks)
        var itemsToAdd = items
            .Where(item => !targetNode.Items.Any(x => x.Title == item.Title))
            .ToList();

        if (itemsToAdd.Count == 0) return;

        ApplyEffectiveDefaultEmulator(targetNode, itemsToAdd);
        ApplyEffectiveParentalProtection(targetNode, itemsToAdd);

        await UiThreadHelper.InvokeAsync(() =>
        {
            InsertMediaItemsOptimized(targetNode.Items, itemsToAdd);

            _libraryTracker.MarkDirty();

            if (IsNodeInCurrentView(targetNode))
                UpdateContent();
        });

        await SaveData();
    }

    private async Task ImportEpicAsync(MediaNode? targetNode)
    {
        if (targetNode == null) targetNode = SelectedNode;
        if (targetNode == null || CurrentWindow is not { } owner) return;

        var discoveredHeroicConfigs = new List<string>();
        var items = await _storeService.ImportHeroicEpicAsync(discoveredConfigPaths: discoveredHeroicConfigs);
        if (items.Count == 0)
        {
            var tryManual = await ShowConfirmDialog(owner, Strings.Dialog_NoEpicInstallationsFound_SelectPath);
            if (!tryManual) return;

            var storageProvider = StorageProvider ?? owner.StorageProvider;
            var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Strings.Dialog_SelectHeroicEpicFolder,
                AllowMultiple = false
            });

            if (folders.Count == 0) return;
            var manualPath = folders[0].Path.LocalPath;

            discoveredHeroicConfigs.Clear();
            items = await _storeService.ImportHeroicEpicAsync(manualPath, discoveredHeroicConfigs);

            if (items.Count == 0)
            {
                await ShowConfirmDialog(owner, Strings.Dialog_NoEpicInstallationsFound);
                return;
            }
        }

        var message = string.Format(Strings.Dialog_ConfirmImportEpicFormat, items.Count);
        if (!await ShowConfirmDialog(owner, message))
            return;

        StoreHeroicEpicConfigPaths(discoveredHeroicConfigs);

        var itemsToAdd = items
            .Where(item => !targetNode.Items.Any(x => x.Title == item.Title))
            .ToList();

        if (itemsToAdd.Count == 0) return;

        ApplyEffectiveDefaultEmulator(targetNode, itemsToAdd);
        ApplyEffectiveParentalProtection(targetNode, itemsToAdd);

        await UiThreadHelper.InvokeAsync(() =>
        {
            InsertMediaItemsOptimized(targetNode.Items, itemsToAdd);

            _libraryTracker.MarkDirty();

            if (IsNodeInCurrentView(targetNode))
                UpdateContent();
        });

        await SaveData();
    }

    private void StoreHeroicEpicConfigPaths(IEnumerable<string> configPaths)
    {
        if (configPaths == null)
            return;

        _currentSettings.HeroicEpicConfigPaths ??= new List<string>();

        var existing = new HashSet<string>(_currentSettings.HeroicEpicConfigPaths, StringComparer.OrdinalIgnoreCase);
        var changed = false;

        foreach (var path in configPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch
            {
                continue;
            }

            if (string.Equals(Path.GetFileName(fullPath), "installed.json", StringComparison.OrdinalIgnoreCase))
            {
                var parent = Directory.GetParent(fullPath)?.FullName;
                if (!string.IsNullOrWhiteSpace(parent))
                    fullPath = parent;
            }

            if (existing.Add(fullPath))
            {
                _currentSettings.HeroicEpicConfigPaths.Add(fullPath);
                changed = true;
            }
        }

        if (changed)
            SaveSettingsOnly();
    }

    private void StoreSteamLibraryPaths(IEnumerable<string> steamAppsPaths)
    {
        if (steamAppsPaths == null)
            return;

        _currentSettings.SteamLibraryPaths ??= new List<string>();

        var existing = new HashSet<string>(_currentSettings.SteamLibraryPaths, StringComparer.OrdinalIgnoreCase);
        var changed = false;

        foreach (var path in steamAppsPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch
            {
                continue;
            }

            if (existing.Add(fullPath))
            {
                _currentSettings.SteamLibraryPaths.Add(fullPath);
                changed = true;
            }
        }

        if (changed)
            SaveSettingsOnly();
    }

    // --- Media & Scraping Actions ---

    private async Task AddMediaAsync(MediaNode? node)
    {
        var targetNode = node ?? SelectedNode;
        if (SelectedNode != null && targetNode != null && targetNode.Id == SelectedNode.Id && targetNode != SelectedNode)
            targetNode = SelectedNode;

        if (targetNode == null || CurrentWindow is not { } owner) return;

        var storageProvider = StorageProvider ?? owner.StorageProvider;
        var result = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Ctx_Media_Add,
            AllowMultiple = true
        });

        if (result == null || result.Count == 0) return;

        var nodePath = PathHelper.GetNodePath(targetNode, RootItems);
        var effectiveDefaultEmulatorId = ResolveEffectiveDefaultEmulatorId(targetNode);
        var defaultMediaType = string.IsNullOrWhiteSpace(effectiveDefaultEmulatorId)
            ? MediaType.Native
            : MediaType.Emulator;

        static bool TryMakeDataRelativeIfInsideDataRoot(string absolutePath, out string relativePath)
        {
            relativePath = string.Empty;

            if (string.IsNullOrWhiteSpace(absolutePath))
                return false;

            if (!Path.IsPathRooted(absolutePath))
                return false;

            var dataRoot = Path.GetFullPath(AppPaths.DataRoot);
            var dataRootWithSep = dataRoot.EndsWith(Path.DirectorySeparatorChar)
                ? dataRoot
                : dataRoot + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(absolutePath);

            if (string.Equals(fullPath, dataRoot, StringComparison.Ordinal) ||
                fullPath.StartsWith(dataRootWithSep, StringComparison.Ordinal))
            {
                relativePath = Path.GetRelativePath(dataRoot, fullPath);
                return true;
            }

            return false;
        }

        // 1) Build new items (UI-free)
        var itemsToAdd = new List<MediaItem>(result.Count);

        var usePortablePaths = _currentSettings.PreferPortableLaunchPaths;

        if (result.Count > 1)
        {
            var combine = await ShowConfirmDialog(owner,
                string.Format(Strings.Dialog_ConfirmCombineMultiDiscFormat, result.Count));

            if (combine)
            {
                var first = result[0];
                var rawTitle = Path.GetFileNameWithoutExtension(first.Name);
                var title = await PromptForName(owner, $"{Strings.Common_Title} '{first.Name}':") ?? rawTitle;
                if (string.IsNullOrWhiteSpace(title)) title = rawTitle;

                // Build file refs with "smart" disc detection from filenames
                var temp = result
                    .Select(f =>
                    {
                        var baseName = Path.GetFileNameWithoutExtension(f.Name);
                        var (idx, label) = TryGetDiscInfoFromFileName(baseName);
                        return (File: f, Index: idx, Label: label);
                    })
                    .ToList();

                // Stable ordering: Index ascending when available, else by filename
                var ordered = temp
                    .OrderBy(x => x.Index ?? int.MaxValue)
                    .ThenBy(x => x.File.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var files = new List<MediaFileRef>(ordered.Count);
                for (int i = 0; i < ordered.Count; i++)
                {
                    var fallbackIndex = i + 1;
                    var rawPath = ordered[i].File.Path.LocalPath;
                    var storedPath = rawPath;
                    var storedKind = MediaFileKind.Absolute;

                    if (usePortablePaths &&
                        TryMakeDataRelativeIfInsideDataRoot(rawPath, out var relativePath))
                    {
                        storedPath = relativePath;
                        storedKind = MediaFileKind.LibraryRelative;
                    }

                    files.Add(new MediaFileRef
                    {
                        Kind = storedKind,
                        Path = storedPath,
                        Index = ordered[i].Index ?? fallbackIndex,
                        Label = ordered[i].Label ?? $"Disc {fallbackIndex}"
                    });
                }

                itemsToAdd.Add(new MediaItem
                {
                    Title = title,
                    Files = files,
                    MediaType = defaultMediaType
                });
            }
            else
            {
                foreach (var file in result)
                {
                    var rawTitle = Path.GetFileNameWithoutExtension(file.Name);
                    var title = await PromptForName(owner, $"{Strings.Common_Title} '{file.Name}':") ?? rawTitle;
                    if (string.IsNullOrWhiteSpace(title)) title = rawTitle;
                    var rawPath = file.Path.LocalPath;
                    var storedPath = rawPath;
                    var storedKind = MediaFileKind.Absolute;

                    if (usePortablePaths &&
                        TryMakeDataRelativeIfInsideDataRoot(rawPath, out var relativePath))
                    {
                        storedPath = relativePath;
                        storedKind = MediaFileKind.LibraryRelative;
                    }

                    itemsToAdd.Add(new MediaItem
                    {
                        Title = title,
                        Files = new List<MediaFileRef>
                        {
                            new()
                            {
                                Kind = storedKind,
                                Path = storedPath,
                                Index = 1,
                                Label = "Disc 1"
                            }
                        },
                        MediaType = defaultMediaType
                    });
                }
            }
        }
        else
        {
            var file = result[0];
            var rawTitle = Path.GetFileNameWithoutExtension(file.Name);
            var title = await PromptForName(owner, $"{Strings.Common_Title} '{file.Name}':") ?? rawTitle;
            if (string.IsNullOrWhiteSpace(title)) title = rawTitle;
            var rawPath = file.Path.LocalPath;
            var storedPath = rawPath;
            var storedKind = MediaFileKind.Absolute;

            if (usePortablePaths &&
                TryMakeDataRelativeIfInsideDataRoot(rawPath, out var relativePath))
            {
                storedPath = relativePath;
                storedKind = MediaFileKind.LibraryRelative;
            }

            itemsToAdd.Add(new MediaItem
            {
                Title = title,
                Files = new List<MediaFileRef>
                {
                    new()
                    {
                        Kind = storedKind,
                        Path = storedPath,
                        Index = 1,
                        Label = "Disc 1"
                    }
                },
                MediaType = defaultMediaType
            });
        }

        if (itemsToAdd.Count == 0) return;

        ApplyEffectiveParentalProtection(targetNode, itemsToAdd);

        // 2) Scan assets off-thread (filesystem only)
        var scanned = await Task.Run(() =>
        {
            var list = new List<(MediaItem Item, List<MediaAsset> Assets)>(itemsToAdd.Count);

            foreach (var item in itemsToAdd)
            {
                var assets = _fileService.ScanItemAssets(item, nodePath);
                list.Add((item, assets));
            }

            return list;
        });

        // Keep the list of actually inserted items outside the UI callback
        var newlyAddedItems = new List<MediaItem>(scanned.Count);
                
        // 3) Apply on UI thread: add items + assets
        await UiThreadHelper.InvokeAsync(() =>
        {
            var newItems = scanned.Select(entry => entry.Item).ToList();
            InsertMediaItemsOptimized(targetNode.Items, newItems);

            foreach (var (item, assets) in scanned)
            {
                newlyAddedItems.Add(item);

                item.Assets.Clear();
                foreach (var asset in assets)
                    item.Assets.Add(asset);
            }

            _libraryTracker.MarkDirty();
        });

        // 4) Remember the last created item as the "selectable" ID
        if (newlyAddedItems.Count > 0)
        {
            var lastItem = newlyAddedItems[^1];
            _currentSettings.LastSelectedMediaId = lastItem.Id;
            RememberNodeSelection(targetNode.Id, lastItem.Id);
            SaveSettingsOnly();
        }
        
        // 5) Refresh the central view only if this node is currently shown
        if (IsNodeInCurrentView(targetNode))
        {
            // Wait until SelectedNodeContent is actually rebuilt
            await UpdateContentAsync();
        }

        // 6) Persist to disk (library + settings)
        await SaveData();
    }

    private async Task EditMediaAsync(MediaItem? item)
    {
        if (item == null || CurrentWindow is not { } owner) return;
        
        var inherited = FindInheritedEmulator(item);
        
        // Find parent node to build the correct path for the FileService (organization)
        var parentNode = FindParentNode(RootItems, item);
        var nodePath = parentNode != null 
            ? PathHelper.GetNodePath(parentNode, RootItems) 
            : new List<string>();

        // Inject FileService and NodePath
        var editVm = new EditMediaViewModel(item, _currentSettings, _fileService, nodePath, inherited, RootItems, parentNode) 
        { 
            StorageProvider = StorageProvider ?? owner.StorageProvider 
        };
        
        var dialog = new EditMediaView { DataContext = editVm };
        
        // bool? so that "X" (no result) remains clearly identifiable
        var result = await dialog.ShowDialog<bool?>(owner);
        
        if (result == true)
        {
            _libraryTracker.MarkDirty();
            await SaveData();

            if (parentNode != null && IsNodeInCurrentView(parentNode))
                UpdateContent();
        }
    }

    private async Task SetMusicAsync(MediaItem? item) 
    {
        if (item == null || CurrentWindow is not { } owner) return;
        
        var parentNode = FindParentNode(RootItems, item) ?? SelectedNode;
        if (parentNode == null) return;

        var storageProvider = StorageProvider ?? owner.StorageProvider;
        var result = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Dialog_Select_Music,
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Audio") { Patterns = new[] { "*.mp3", "*.wav", "*.ogg", "*.flac", "*.sid" } } }
        });

        if (result != null && result.Count == 1)
        {
            _audioService.StopMusic();
            var sourceFile = result[0].Path.LocalPath;
            var nodePath = PathHelper.GetNodePath(parentNode, RootItems);
            
            // Use AssetType.Music instead of a media file kind enum.
            var asset = await _fileService.ImportAssetAsync(sourceFile, item, nodePath, AssetType.Music);

            if (asset != null)
            {
                await UiThreadHelper.InvokeAsync(() => item.Assets.Add(asset));

                _libraryTracker.MarkDirty();

                var fullPath = AppPaths.ResolveDataPathInsideRootOrEmpty(asset.RelativePath);
                if (!string.IsNullOrWhiteSpace(fullPath))
                    _ = _audioService.PlayMusicAsync(fullPath);

                await SaveData();
            }
        }
    }

    private async Task ScrapeMediaAsync(MediaItem? item)
    {
        if (item == null || CurrentWindow is not { } owner) return;

        var parentNode = FindParentNode(RootItems, item) ?? SelectedNode;
        if (parentNode == null) return;

        var nodePath = PathHelper.GetNodePath(parentNode, RootItems);
        var importSettings = GetScraperImportSettings();

        var vm = new ScrapeDialogViewModel(item, _currentSettings, _metadataService);
        
        vm.OnResultSelectedAsync += async (result) => 
        {
            var changed = false;

            if (await ApplyScrapedMetadataAsync(owner, item, result, importSettings, allowConflictPrompts: true))
                changed = true;

            if (await ApplyScrapedAssetsAsync(
                    owner,
                    item,
                    result,
                    nodePath,
                    importSettings,
                    allowConflictPromptsForAssets: true,
                    appendAssetsOnConflictWithoutPrompt: importSettings.AppendAssetsDuringBulkScrape))
                changed = true;

            if (changed)
            {
                _libraryTracker.MarkDirty();
                await SaveData();
            }
            
            // Close dialog manually if needed (finding window by DataContext)
            if (owner.OwnedWindows.FirstOrDefault(w => w.DataContext == vm) is Window dlg) dlg.Close();
        };

        var dialog = new ScrapeDialogView { DataContext = vm };
        await dialog.ShowDialog(owner);
    }

    private async Task ScrapeNodeAsync(MediaNode? node)
    {
        if (SelectedNode != null && node != null && node.Id == SelectedNode.Id && node != SelectedNode) node = SelectedNode;
        if (node == null) node = SelectedNode;
        if (node == null || CurrentWindow is not { } owner) return;
        var importSettings = GetScraperImportSettings();

        var vm = new BulkScrapeViewModel(node, _currentSettings, _metadataService);
        vm.OnItemScrapedAsync = async (item, result) =>
        {
            var parent = FindParentNode(RootItems, item);
            if (parent == null) return;
            var nodePath = PathHelper.GetNodePath(parent, RootItems);
    
            var changed = false;

            if (await ApplyScrapedMetadataAsync(owner, item, result, importSettings, allowConflictPrompts: false))
                changed = true;

            if (await ApplyScrapedAssetsAsync(
                    owner,
                    item,
                    result,
                    nodePath,
                    importSettings,
                    allowConflictPromptsForAssets: false,
                    appendAssetsOnConflictWithoutPrompt: importSettings.AppendAssetsDuringBulkScrape))
                changed = true;
            
            if (changed)
                _libraryTracker.MarkDirty();
        };
    
        var dialog = new BulkScrapeView { DataContext = vm };
        await dialog.ShowDialog(owner);
        await SaveData();
        if (IsNodeInCurrentView(node)) UpdateContent();
    }

    private ScraperImportSettings GetScraperImportSettings()
    {
        _currentSettings.ScraperImport ??= new ScraperImportSettings();
        return _currentSettings.ScraperImport;
    }

    private async Task<bool> ApplyScrapedMetadataAsync(
        Window owner,
        MediaItem item,
        ScraperSearchResult result,
        ScraperImportSettings settings,
        bool allowConflictPrompts)
    {
        var changed = false;
        var mode = settings.ExistingDataMode;

        if (settings.ImportDescription &&
            await TryApplyStringFieldAsync(
                owner,
                T("Common.Description", "Description"),
                item.Description,
                result.Description,
                value => item.Description = value,
                allowConflictPrompts,
                mode,
                StringComparison.Ordinal))
        {
            changed = true;
        }

        if (settings.ImportReleaseDate &&
            await TryApplyDateFieldAsync(
                owner,
                T("Common.ReleaseDate", "Release Date"),
                item.ReleaseDate,
                result.ReleaseDate,
                value => item.ReleaseDate = value,
                allowConflictPrompts,
                mode))
        {
            changed = true;
        }

        if (settings.ImportRating &&
            await TryApplyRatingFieldAsync(
                owner,
                T("Common.Rating", "Rating"),
                item.Rating,
                result.Rating,
                value => item.Rating = value,
                allowConflictPrompts,
                mode))
        {
            changed = true;
        }

        if (settings.ImportDeveloper &&
            await TryApplyStringFieldAsync(
                owner,
                T("Common.Developer", "Developer"),
                item.Developer,
                result.Developer,
                value => item.Developer = value,
                allowConflictPrompts,
                mode))
        {
            changed = true;
        }

        if (settings.ImportGenre &&
            await TryApplyStringFieldAsync(
                owner,
                T("Common.Genre", "Genre"),
                item.Genre,
                result.Genre,
                value => item.Genre = value,
                allowConflictPrompts,
                mode))
        {
            changed = true;
        }

        if (settings.ImportPlatform)
        {
            if (await TryApplyStringFieldAsync(
                    owner,
                    T("Common.Platform", "Platform"),
                    item.Platform,
                    result.Platform,
                    value => item.Platform = value,
                    allowConflictPrompts,
                    mode))
            {
                changed = true;
            }

            var platform = result.Platform?.Trim();
            if (!string.IsNullOrWhiteSpace(platform) &&
                string.Equals(item.Platform?.Trim(), platform, StringComparison.OrdinalIgnoreCase) &&
                !item.Tags.Any(t => string.Equals(t, platform, StringComparison.OrdinalIgnoreCase)))
            {
                item.Tags.Add(platform);
                changed = true;
            }
        }

        if (settings.ImportPublisher &&
            await TryApplyStringFieldAsync(
                owner,
                T("Common.Publisher", "Publisher"),
                item.Publisher,
                result.Publisher,
                value => item.Publisher = value,
                allowConflictPrompts,
                mode))
        {
            changed = true;
        }

        if (settings.ImportSeries &&
            await TryApplyStringFieldAsync(
                owner,
                T("Common.Series", "Series"),
                item.Series,
                result.Series,
                value => item.Series = value,
                allowConflictPrompts,
                mode))
        {
            changed = true;
        }

        if (settings.ImportReleaseType &&
            await TryApplyStringFieldAsync(
                owner,
                T("Common.ReleaseType", "Release Type"),
                item.ReleaseType,
                result.ReleaseType,
                value => item.ReleaseType = value,
                allowConflictPrompts,
                mode))
        {
            changed = true;
        }

        if (settings.ImportSortTitle &&
            await TryApplyStringFieldAsync(
                owner,
                T("Common.SortTitle", "Sort Title"),
                item.SortTitle,
                result.SortTitle,
                value => item.SortTitle = value,
                allowConflictPrompts,
                mode))
        {
            changed = true;
        }

        if (settings.ImportPlayMode &&
            await TryApplyStringFieldAsync(
                owner,
                T("Common.PlayMode", "Play Mode"),
                item.PlayMode,
                result.PlayMode,
                value => item.PlayMode = value,
                allowConflictPrompts,
                mode))
        {
            changed = true;
        }

        if (settings.ImportMaxPlayers &&
            await TryApplyStringFieldAsync(
                owner,
                T("Common.MaxPlayers", "Max Players"),
                item.MaxPlayers,
                result.MaxPlayers,
                value => item.MaxPlayers = value,
                allowConflictPrompts,
                mode))
        {
            changed = true;
        }

        if (settings.ImportSource &&
            await TryApplyStringFieldAsync(
                owner,
                T("Common.Source", "Source"),
                item.Source,
                result.Source,
                value => item.Source = value,
                allowConflictPrompts,
                mode))
        {
            changed = true;
        }

        if (settings.ImportCustomFields &&
            await MergeCustomFieldsAsync(owner, item, result.CustomFields, allowConflictPrompts, mode))
        {
            changed = true;
        }

        return changed;
    }

    private async Task<bool> ApplyScrapedAssetsAsync(
        Window owner,
        MediaItem item,
        ScraperSearchResult result,
        List<string> nodePath,
        ScraperImportSettings settings,
        bool allowConflictPromptsForAssets,
        bool appendAssetsOnConflictWithoutPrompt)
    {
        var changed = false;
        var mode = settings.ExistingDataMode;

        if (await TryImportScrapedAssetAsync(owner, item, nodePath, AssetType.Cover, result.CoverUrl, settings.ImportCover, mode, T("Button.Cover", "Cover"), allowConflictPromptsForAssets, appendAssetsOnConflictWithoutPrompt))
            changed = true;

        if (await TryImportScrapedAssetAsync(owner, item, nodePath, AssetType.Wallpaper, result.WallpaperUrl, settings.ImportWallpaper, mode, T("NodeSettings_ArtworkWallpaperLabel", "Wallpaper"), allowConflictPromptsForAssets, appendAssetsOnConflictWithoutPrompt))
            changed = true;

        if (await TryImportScrapedAssetAsync(owner, item, nodePath, AssetType.Screenshot, result.ScreenshotUrl, settings.ImportScreenshot, mode, T("Button.Screenshot", "Screenshot"), allowConflictPromptsForAssets, appendAssetsOnConflictWithoutPrompt))
            changed = true;

        if (await TryImportScrapedAssetAsync(owner, item, nodePath, AssetType.Logo, result.LogoUrl, settings.ImportLogo, mode, T("Button.Logo", "Logo"), allowConflictPromptsForAssets, appendAssetsOnConflictWithoutPrompt))
            changed = true;

        if (await TryImportScrapedAssetAsync(owner, item, nodePath, AssetType.Marquee, result.MarqueeUrl, settings.ImportMarquee, mode, T("NodeSettings_ArtworkMarqueeLabel", "Marquee"), allowConflictPromptsForAssets, appendAssetsOnConflictWithoutPrompt))
            changed = true;

        if (await TryImportScrapedAssetAsync(owner, item, nodePath, AssetType.Bezel, result.BezelUrl, settings.ImportBezel, mode, T("Button.Bezel", "Bezel"), allowConflictPromptsForAssets, appendAssetsOnConflictWithoutPrompt))
            changed = true;

        if (await TryImportScrapedAssetAsync(owner, item, nodePath, AssetType.ControlPanel, result.ControlPanelUrl, settings.ImportControlPanel, mode, T("Button.ControlPanel", "Control panel"), allowConflictPromptsForAssets, appendAssetsOnConflictWithoutPrompt))
            changed = true;

        return changed;
    }

    private async Task<bool> TryImportScrapedAssetAsync(
        Window owner,
        MediaItem item,
        List<string> nodePath,
        AssetType type,
        string? url,
        bool isEnabled,
        ScraperExistingDataMode mode,
        string assetLabel,
        bool allowConflictPromptsForAssets,
        bool appendAssetsOnConflictWithoutPrompt)
    {
        if (!isEnabled || string.IsNullOrWhiteSpace(url))
            return false;

        var hasExisting = item.Assets.Any(a => a.Type == type);
        if (hasExisting)
        {
            if (appendAssetsOnConflictWithoutPrompt)
                return await DownloadAndSetAsset(url, item, nodePath, type);

            if (!allowConflictPromptsForAssets)
                return false;

            if (mode == ScraperExistingDataMode.OnlyMissing)
                return false;

            if (mode == ScraperExistingDataMode.AskOnConflict)
            {
                var message = string.Format(
                    T("Dialog.Scraper.AssetConflictFormat", "Item already has {0}. Add another one?"),
                    assetLabel);

                if (!await ShowConfirmDialog(owner, message))
                    return false;
            }
        }

        return await DownloadAndSetAsset(url, item, nodePath, type);
    }

    private async Task<bool> TryApplyStringFieldAsync(
        Window owner,
        string fieldLabel,
        string? currentValue,
        string? incomingValue,
        Action<string> applyValue,
        bool allowConflictPrompts,
        ScraperExistingDataMode mode,
        StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        var incoming = incomingValue?.Trim();
        if (string.IsNullOrWhiteSpace(incoming))
            return false;

        var current = currentValue?.Trim();
        if (string.IsNullOrWhiteSpace(current))
        {
            applyValue(incoming);
            return true;
        }

        if (string.Equals(current, incoming, comparison))
            return false;

        if (!await ShouldOverwriteExistingAsync(owner, fieldLabel, current, incoming, allowConflictPrompts, mode))
            return false;

        applyValue(incoming);
        return true;
    }

    private async Task<bool> TryApplyDateFieldAsync(
        Window owner,
        string fieldLabel,
        DateTime? currentValue,
        DateTime? incomingValue,
        Action<DateTime?> applyValue,
        bool allowConflictPrompts,
        ScraperExistingDataMode mode)
    {
        if (!incomingValue.HasValue)
            return false;

        var incoming = incomingValue.Value.Date;
        if (!currentValue.HasValue)
        {
            applyValue(incoming);
            return true;
        }

        var current = currentValue.Value.Date;
        if (current == incoming)
            return false;

        if (!await ShouldOverwriteExistingAsync(
                owner,
                fieldLabel,
                current.ToShortDateString(),
                incoming.ToShortDateString(),
                allowConflictPrompts,
                mode))
        {
            return false;
        }

        applyValue(incoming);
        return true;
    }

    private async Task<bool> TryApplyRatingFieldAsync(
        Window owner,
        string fieldLabel,
        double currentValue,
        double? incomingValue,
        Action<double> applyValue,
        bool allowConflictPrompts,
        ScraperExistingDataMode mode)
    {
        if (!incomingValue.HasValue)
            return false;

        var incoming = Math.Clamp(incomingValue.Value, 0d, 100d);
        var hasCurrent = currentValue > 0d;
        if (!hasCurrent)
        {
            applyValue(incoming);
            return true;
        }

        if (Math.Abs(currentValue - incoming) < 0.0001d)
            return false;

        if (!await ShouldOverwriteExistingAsync(
                owner,
                fieldLabel,
                currentValue.ToString("0.##"),
                incoming.ToString("0.##"),
                allowConflictPrompts,
                mode))
        {
            return false;
        }

        applyValue(incoming);
        return true;
    }

    private async Task<bool> MergeCustomFieldsAsync(
        Window owner,
        MediaItem item,
        Dictionary<string, string>? incoming,
        bool allowConflictPrompts,
        ScraperExistingDataMode mode)
    {
        if (incoming == null || incoming.Count == 0)
            return false;

        var merged = new Dictionary<string, string>(
            item.CustomFields ?? new Dictionary<string, string>(StringComparer.Ordinal),
            StringComparer.Ordinal);

        var changed = false;

        foreach (var kv in incoming)
        {
            var key = kv.Key?.Trim();
            var value = kv.Value?.Trim();
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                continue;

            if (!merged.TryGetValue(key, out var existing) || string.IsNullOrWhiteSpace(existing))
            {
                merged[key] = value;
                changed = true;
                continue;
            }

            if (string.Equals(existing, value, StringComparison.Ordinal))
                continue;

            if (mode == ScraperExistingDataMode.OnlyMissing)
                continue;

            if (mode == ScraperExistingDataMode.AskOnConflict)
            {
                if (!allowConflictPrompts)
                    continue;

                var message = string.Format(
                    T("Dialog.Scraper.CustomFieldConflictFormat", "Update custom field '{0}'? Current: '{1}' | New: '{2}'"),
                    key,
                    BuildConflictPreview(existing),
                    BuildConflictPreview(value));

                if (!await ShowConfirmDialog(owner, message))
                    continue;
            }

            merged[key] = value;
            changed = true;
        }

        if (!changed)
            return false;

        item.CustomFields = merged;
        return true;
    }

    private async Task<bool> ShouldOverwriteExistingAsync(
        Window owner,
        string fieldLabel,
        string currentValue,
        string incomingValue,
        bool allowConflictPrompts,
        ScraperExistingDataMode mode)
    {
        if (mode == ScraperExistingDataMode.OverwriteAlways)
            return true;

        if (mode == ScraperExistingDataMode.OnlyMissing)
            return false;

        if (!allowConflictPrompts)
            return false;

        var message = string.Format(
            T("Dialog.Scraper.FieldConflictFormat", "Update {0}? Current: '{1}' | New: '{2}'"),
            fieldLabel.Trim().TrimEnd(':'),
            BuildConflictPreview(currentValue),
            BuildConflictPreview(incomingValue));

        return await ShowConfirmDialog(owner, message);
    }

    private static string BuildConflictPreview(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "-";

        var compact = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return compact.Length <= 80
            ? compact
            : compact.Substring(0, 80) + "...";
    }

    private async Task<bool> DownloadAndSetAsset(string url, MediaItem item, List<string> nodePath, AssetType type)
    {
        string? tempPathWithExt = null;
        try
        {
            var tempFile = Path.GetTempFileName();
            var ext = Path.GetExtension(url).Split('?')[0];
            if (string.IsNullOrEmpty(ext)) ext = ".jpg";
            tempPathWithExt = Path.ChangeExtension(tempFile, ext);
            
            if (File.Exists(tempPathWithExt)) File.Delete(tempPathWithExt);
            File.Move(tempFile, tempPathWithExt);

            bool success = false;

            if (await AsyncImageHelper.SaveCachedImageAsync(url, tempPathWithExt)) 
            {
                success = true;
            }
            else
            {
                try
                {
                    // Use DI-managed HttpClient (configured in App: timeout + user-agent)
                    var data = await _httpClient.GetByteArrayAsync(url);
                    await File.WriteAllBytesAsync(tempPathWithExt, data);
                    success = true;
                }
                catch (Exception ex) 
                { 
                    Debug.WriteLine($"Download Failed: {ex.Message}"); 
                }
            }

            if (success)
            {
                var imported = await _fileService.ImportAssetAsync(tempPathWithExt, item, nodePath, type);
                if (imported != null)
                {
                    await UiThreadHelper.InvokeAsync(() => item.Assets.Add(imported));
                    return true;
                }
            }
        }
        catch (Exception ex) 
        { 
            Debug.WriteLine($"Critical Download Error: {ex.Message}"); 
        }
        finally
        {
            try
            {
                if (!string.IsNullOrEmpty(tempPathWithExt) && File.Exists(tempPathWithExt))
                    File.Delete(tempPathWithExt);
            }
            catch
            {
                // best effort cleanup
            }
        }

        return false;
    }

    private void OpenIntegratedSearch()
    {
        // Toggle behavior: pressing search while search is already open jumps back.
        if (SelectedNodeContent is SearchAreaViewModel activeSearchVm)
        {
            CloseIntegratedSearch(activeSearchVm);
            return;
        }

        if (_currentMediaAreaVm != null)
        {
            _searchUiState.SharedSearchText = _currentMediaAreaVm.SearchText ?? string.Empty;
            _searchUiState.SharedOnlyFavorites = _currentMediaAreaVm.OnlyFavorites;
        }

        // Remember where the user came from so we can restore it on next toggle.
        _searchReturnNodeId = SelectedNode?.Id;
        _searchReturnItemId = _currentMediaAreaVm?.SelectedMediaItem?.Id;
        _pendingGlobalSearchSelectionItemId = _searchReturnItemId;

        // Ensure any previous media-area handlers are detached before switching views.
        DetachMediaAreaHandlers();
        DetachSearchAreaHandlers();

        _selectedNode = null;
        // Force refresh of selection
        OnPropertyChanged(nameof(SelectedNode));
        
        var searchVm = new SearchAreaViewModel(RootItems, IsParentalFilterActive) { ItemWidth = ItemWidth };
        searchVm.OnlyFavorites = _searchUiState.SharedOnlyFavorites;
        searchVm.SearchText = _searchUiState.SharedSearchText;
        if (_searchUiState.HasGlobalScopeSelection)
            searchVm.ApplyScopeSelection(_searchUiState.GlobalScopeNodeIds);
        AttachSearchAreaHandlers(searchVm);
        SelectedNodeContent = searchVm;
    }

    private void CloseIntegratedSearch(SearchAreaViewModel searchVm)
    {
        _searchUiState.SharedSearchText = searchVm.SearchText ?? string.Empty;
        var selectedSearchItemId = searchVm.SelectedMediaItem?.Id;
        var desiredItemId = selectedSearchItemId ?? _searchReturnItemId;

        MediaNode? targetNode = null;

        if (!string.IsNullOrWhiteSpace(selectedSearchItemId))
            TryFindNodeByMediaId(RootItems, selectedSearchItemId, out targetNode);

        if (targetNode == null && !string.IsNullOrWhiteSpace(_searchReturnNodeId))
            targetNode = FindNodeById(RootItems, _searchReturnNodeId);

        if (targetNode == null && !string.IsNullOrWhiteSpace(desiredItemId))
            TryFindNodeByMediaId(RootItems, desiredItemId, out targetNode);

        if (targetNode != null && !targetNode.IsVisibleInTree)
            targetNode = null;

        if (targetNode == null)
            targetNode = FindFirstVisibleNode();

        // Leave search mode now (dispose search VM) before restoring content.
        DetachSearchAreaHandlers();

        // Clear remembered return state once the toggle-back was requested.
        _searchReturnNodeId = null;
        _searchReturnItemId = null;

        if (targetNode == null)
        {
            _restoreSearchUiStateOnNextContentBuild = false;
            _pendingGlobalSearchSelectionItemId = null;
            _selectedNode = null;
            OnPropertyChanged(nameof(SelectedNode));
            SelectedNodeContent = null;
            _audioService.StopMusic();

            OnPropertyChanged(nameof(ResolvedSelectedItemLogoPath));
            OnPropertyChanged(nameof(ResolvedSelectedItemWallpaperPath));
            OnPropertyChanged(nameof(ResolvedSelectedItemVideoPath));
            OnPropertyChanged(nameof(ResolvedSelectedItemMarqueePath));
            return;
        }

        if (!string.IsNullOrWhiteSpace(desiredItemId))
            RememberNodeSelection(targetNode.Id, desiredItemId);

        _restoreSearchUiStateOnNextContentBuild = true;
        ExpandPathToNode(RootItems, targetNode);
        SelectedNode = targetNode;
    }

    // Wrappers for Assets
    private async Task SetCoverAsync(MediaItem? item) => await SetAssetAsync(item, Strings.Dialog_Select_Cover, AssetType.Cover);
    private async Task SetLogoAsync(MediaItem? item) => await SetAssetAsync(item, Strings.Dialog_Select_Logo, AssetType.Logo);
    private async Task SetWallpaperAsync(MediaItem? item) => await SetAssetAsync(item, Strings.Dialog_Select_Wallpaper, AssetType.Wallpaper);

    private EmulatorConfig? FindInheritedEmulator(MediaItem item)
    {
        var parentNode = FindParentNode(RootItems, item);
        if (parentNode == null) return null;
        var nodeChain = PathHelper.GetNodeChain(parentNode, RootItems, matchById: true);
        nodeChain.Reverse();
        foreach (var node in nodeChain)
            if (!string.IsNullOrEmpty(node.DefaultEmulatorId))
                return _currentSettings.Emulators.FirstOrDefault(e => e.Id == node.DefaultEmulatorId);
        return null;
    }

    private string? ResolveEffectiveDefaultEmulatorId(MediaNode targetNode)
    {
        var chain = PathHelper.GetNodeChain(targetNode, RootItems, matchById: true);
        chain.Reverse();
        return chain.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n.DefaultEmulatorId))?.DefaultEmulatorId;
    }

    private void ApplyEffectiveDefaultEmulator(MediaNode targetNode, IEnumerable<MediaItem> items)
    {
        var effectiveDefaultEmulatorId = ResolveEffectiveDefaultEmulatorId(targetNode);
        if (string.IsNullOrWhiteSpace(effectiveDefaultEmulatorId))
            return;

        foreach (var item in items)
        {
            if (item.MediaType != MediaType.Native)
                continue;

            if (!string.IsNullOrWhiteSpace(item.EmulatorId))
                continue;

            if (!string.IsNullOrWhiteSpace(item.LauncherPath))
                continue;

            item.MediaType = MediaType.Emulator;
        }
    }

    private async Task RescanAllAssetsAsync()
    {
        List<MediaNode> rootNodes = new();
        await UiThreadHelper.InvokeAsync(() =>
        {
            rootNodes = RootItems.ToList();
        });

        var anyChanged = false;

        // Offload recursion and filesystem scanning to a background thread.
        await Task.Run(async () => 
        { 
            foreach (var rootNode in rootNodes) 
            {
                if (await RescanNodeRecursive(rootNode))
                    anyChanged = true;
            }
        });

        if (anyChanged)
            _libraryTracker.MarkDirty();
    }

    private async Task<bool> RescanNodeRecursive(MediaNode node)
    {
        List<string> nodePath = new();
        List<MediaItem> items = new();
        List<MediaNode> children = new();

        await UiThreadHelper.InvokeAsync(() =>
        {
            nodePath = PathHelper.GetNodePath(node, RootItems);
            items = node.Items.ToList();
            children = node.Children.ToList();
        });
        
        // 1) Scan assets off the UI thread (filesystem only).
        var scanned = await Task.Run(() =>
        {
            var list = new List<(MediaItem Item, List<MediaAsset> Assets)>(items.Count);

            foreach (var item in items)
            {
                var assets = _fileService.ScanItemAssets(item, nodePath);
                list.Add((item, assets));
            }

            return list;
        });

        var changed = false;

        // 2) Apply results on UI thread in batches to keep the UI responsive for very large nodes.
        const int batchSize = 500;
        for (int i = 0; i < scanned.Count; i += batchSize)
        {
            var start = i;
            var count = Math.Min(batchSize, scanned.Count - start);

            await UiThreadHelper.InvokeAsync(() =>
            {
                for (int j = start; j < start + count; j++)
                {
                    var (item, assets) = scanned[j];

                    if (AssetsMatch(item.Assets, assets))
                        continue;

                    changed = true;

                    item.Assets.Clear();
                    foreach (var asset in assets)
                        item.Assets.Add(asset);
                }
            });

            // Yield between batches so the UI can process input/layout/render.
            await Task.Yield();
        }

        // 3) Recurse children
        foreach (var child in children)
        {
            if (await RescanNodeRecursive(child))
                changed = true;
        }

        return changed;
    }

    private static bool AssetsMatch(IList<MediaAsset> existing, List<MediaAsset> scanned)
    {
        if (existing.Count != scanned.Count)
            return false;

        var set = new HashSet<(AssetType Type, string Path)>(existing.Count);
        foreach (var asset in existing)
        {
            var path = asset.RelativePath ?? string.Empty;
            set.Add((asset.Type, path));
        }

        foreach (var asset in scanned)
        {
            var path = asset.RelativePath ?? string.Empty;
            if (!set.Contains((asset.Type, path)))
                return false;
        }

        return true;
    }
    
    private async Task SetAssetAsync(MediaItem? item, string title, AssetType type)
    {
        if (item == null || CurrentWindow is not { } owner) return;
        
        var parentNode = FindParentNode(RootItems, item) ?? SelectedNode;
        if (parentNode == null) return;
        
        var result = await (StorageProvider ?? owner.StorageProvider).OpenFilePickerAsync(new FilePickerOpenOptions 
        { 
            Title = title, 
            AllowMultiple = false, 
            FileTypeFilter = new[] { FilePickerFileTypes.ImageAll } 
        });
        
        if (result != null && result.Count == 1)
        {
            var asset = await _fileService.ImportAssetAsync(
                result[0].Path.LocalPath,
                item,
                PathHelper.GetNodePath(parentNode, RootItems),
                type);

            if (asset != null)
            {
                await UiThreadHelper.InvokeAsync(() => item.Assets.Add(asset));
                _libraryTracker.MarkDirty();
                await SaveData();
            }
        }
    }
}
