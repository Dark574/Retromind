using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Models;
using Retromind.Services.Stores.Security;

namespace Retromind.Services;

/// <summary>
/// Stores scraper credentials outside the portable settings file and hydrates
/// the runtime scraper profiles from the operating system's persistent keyring.
/// </summary>
public sealed class ScraperCredentialService
{
    private const string ServiceName = "retromind:scraper";
    private readonly IPersistentSecretStore _secretStore;

    public ScraperCredentialService(IPersistentSecretStore secretStore)
    {
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
    }

    /// <summary>
    /// Loads credentials from the keyring and migrates legacy encrypted JSON values.
    /// Legacy values are removed only after a durable write has been read back successfully.
    /// </summary>
    public async Task InitializeAsync(
        AppSettings settings,
        SettingsService settingsService,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(settingsService);

        bool available;
        try
        {
            available = await _secretStore.IsAvailableAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ScraperCredentials] Could not inspect the system keyring: {ex.Message}");
            return;
        }

        if (!available)
        {
            Debug.WriteLine("[ScraperCredentials] Persistent system keyring is unavailable; legacy values remain unchanged.");
            return;
        }

        var settingsChanged = EnsureUniqueProfileIds(settings.Scrapers);

        foreach (var scraper in settings.Scrapers)
        {
            settingsChanged |= await HydrateOrMigrateAsync(
                scraper,
                CredentialKind.ApiKey,
                scraper.EncryptedApiKey,
                value => scraper.ApiKey = value,
                () => scraper.ApiKey,
                () => scraper.EncryptedApiKey = null,
                cancellationToken).ConfigureAwait(false);

            settingsChanged |= await HydrateOrMigrateAsync(
                scraper,
                CredentialKind.Password,
                scraper.EncryptedPassword,
                value => scraper.Password = value,
                () => scraper.Password,
                () => scraper.EncryptedPassword = null,
                cancellationToken).ConfigureAwait(false);

            settingsChanged |= await HydrateOrMigrateAsync(
                scraper,
                CredentialKind.ClientSecret,
                scraper.EncryptedClientSecret,
                value => scraper.ClientSecret = value,
                () => scraper.ClientSecret,
                () => scraper.EncryptedClientSecret = null,
                cancellationToken).ConfigureAwait(false);
        }

        if (settingsChanged)
            await settingsService.SaveAfterSecretMigrationAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies credential edits as a reversible transaction. Callers must complete
    /// the transaction only after the corresponding settings snapshot was persisted.
    /// </summary>
    public async Task<ScraperCredentialTransaction> ApplyChangesAsync(
        IReadOnlyList<ScraperConfig> previousProfiles,
        IReadOnlyList<ScraperConfig> nextProfiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(previousProfiles);
        ArgumentNullException.ThrowIfNull(nextProfiles);

        ValidateProfileIds(nextProfiles);
        var changes = BuildChanges(previousProfiles, nextProfiles);
        if (changes.Count == 0)
            return ScraperCredentialTransaction.Empty(_secretStore);

        if (!await _secretStore.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The persistent system keyring is unavailable.");

        var rollbackValues = new List<CredentialValue>(changes.Count);
        try
        {
            foreach (var change in changes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var oldValue = await _secretStore.GetAsync(change.Key, cancellationToken).ConfigureAwait(false);
                rollbackValues.Add(new CredentialValue(change.Key, oldValue));
                await WriteAndVerifyAsync(change.Key, change.Value, cancellationToken).ConfigureAwait(false);
            }

            ClearMigratedLegacyFields(nextProfiles, changes);

            return new ScraperCredentialTransaction(_secretStore, rollbackValues);
        }
        catch
        {
            await RestoreBestEffortAsync(_secretStore, rollbackValues).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<bool> HydrateOrMigrateAsync(
        ScraperConfig scraper,
        CredentialKind kind,
        string? legacyCiphertext,
        Action<string?> setRuntimeValue,
        Func<string?> getRuntimeValue,
        Action clearLegacyValue,
        CancellationToken cancellationToken)
    {
        try
        {
            var key = CreateKey(scraper.Id, kind);
            var storedValue = await _secretStore.GetAsync(key, cancellationToken).ConfigureAwait(false);
            if (storedValue != null)
            {
                setRuntimeValue(storedValue);
                if (legacyCiphertext == null)
                    return false;

                clearLegacyValue();
                return true;
            }

            if (legacyCiphertext == null)
                return false;

            // Older saves emitted empty encrypted fields for credentials that were
            // never configured. They contain no recoverable value and can be removed.
            if (legacyCiphertext.Length == 0)
            {
                clearLegacyValue();
                return true;
            }

            // SettingsService has already decrypted a valid legacy value into the
            // runtime-only property. An empty result may indicate corrupt ciphertext;
            // retain it for recovery instead of deleting the only persisted copy.
            var legacyPlaintext = getRuntimeValue();
            if (string.IsNullOrEmpty(legacyPlaintext))
                return false;

            await WriteAndVerifyAsync(key, legacyPlaintext, cancellationToken).ConfigureAwait(false);
            clearLegacyValue();
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[ScraperCredentials] Could not load or migrate {kind} for profile {scraper.Id}: {ex.Message}");
            return false;
        }
    }

    private async Task WriteAndVerifyAsync(
        SecretKey key,
        string? value,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(value))
            await _secretStore.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
        else
            await _secretStore.SetAsync(key, value, cancellationToken).ConfigureAwait(false);

        var verified = await _secretStore.GetAsync(key, cancellationToken).ConfigureAwait(false);
        var expected = string.IsNullOrEmpty(value) ? null : value;
        if (!string.Equals(verified, expected, StringComparison.Ordinal))
            throw new InvalidOperationException("The scraper credential could not be verified after writing it.");
    }

    private static List<CredentialValue> BuildChanges(
        IReadOnlyList<ScraperConfig> previousProfiles,
        IReadOnlyList<ScraperConfig> nextProfiles)
    {
        var previousById = previousProfiles
            .Where(profile => !string.IsNullOrWhiteSpace(profile.Id))
            .GroupBy(profile => profile.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var nextById = nextProfiles.ToDictionary(profile => profile.Id, StringComparer.Ordinal);
        var changes = new List<CredentialValue>();

        foreach (var next in nextProfiles)
        {
            previousById.TryGetValue(next.Id, out var previous);
            AddIfChanged(
                changes,
                next.Id,
                CredentialKind.ApiKey,
                previous?.ApiKey,
                next.ApiKey,
                next.EncryptedApiKey != null && !string.IsNullOrEmpty(next.ApiKey));
            AddIfChanged(
                changes,
                next.Id,
                CredentialKind.Password,
                previous?.Password,
                next.Password,
                next.EncryptedPassword != null && !string.IsNullOrEmpty(next.Password));
            AddIfChanged(
                changes,
                next.Id,
                CredentialKind.ClientSecret,
                previous?.ClientSecret,
                next.ClientSecret,
                next.EncryptedClientSecret != null && !string.IsNullOrEmpty(next.ClientSecret));
        }

        foreach (var previous in previousById.Values.Where(profile => !nextById.ContainsKey(profile.Id)))
        {
            changes.Add(new CredentialValue(CreateKey(previous.Id, CredentialKind.ApiKey), null));
            changes.Add(new CredentialValue(CreateKey(previous.Id, CredentialKind.Password), null));
            changes.Add(new CredentialValue(CreateKey(previous.Id, CredentialKind.ClientSecret), null));
        }

        return changes;
    }

    private static void AddIfChanged(
        ICollection<CredentialValue> changes,
        string profileId,
        CredentialKind kind,
        string? previous,
        string? next,
        bool forceWrite = false)
    {
        var normalizedPrevious = string.IsNullOrEmpty(previous) ? null : previous;
        var normalizedNext = string.IsNullOrEmpty(next) ? null : next;
        if (forceWrite || !string.Equals(normalizedPrevious, normalizedNext, StringComparison.Ordinal))
            changes.Add(new CredentialValue(CreateKey(profileId, kind), normalizedNext));
    }

    private static void ClearMigratedLegacyFields(
        IReadOnlyList<ScraperConfig> profiles,
        IReadOnlyList<CredentialValue> changes)
    {
        var changedKeys = changes.Select(change => change.Key).ToHashSet();
        foreach (var profile in profiles)
        {
            if (changedKeys.Contains(CreateKey(profile.Id, CredentialKind.ApiKey)))
                profile.EncryptedApiKey = null;
            if (changedKeys.Contains(CreateKey(profile.Id, CredentialKind.Password)))
                profile.EncryptedPassword = null;
            if (changedKeys.Contains(CreateKey(profile.Id, CredentialKind.ClientSecret)))
                profile.EncryptedClientSecret = null;
        }
    }

    private static bool EnsureUniqueProfileIds(IReadOnlyList<ScraperConfig> profiles)
    {
        var changed = false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in profiles)
        {
            if (!string.IsNullOrWhiteSpace(profile.Id) && ids.Add(profile.Id))
                continue;

            do
            {
                profile.Id = Guid.NewGuid().ToString();
            }
            while (!ids.Add(profile.Id));

            changed = true;
        }

        return changed;
    }

    private static void ValidateProfileIds(IReadOnlyList<ScraperConfig> profiles)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Id) || !ids.Add(profile.Id))
                throw new InvalidOperationException("Scraper profiles must have unique identifiers.");
        }
    }

    private static SecretKey CreateKey(string profileId, CredentialKind kind)
        => new(ServiceName, $"{profileId}:{kind switch
        {
            CredentialKind.ApiKey => "api-key",
            CredentialKind.Password => "password",
            CredentialKind.ClientSecret => "client-secret",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        }}");

    private static async Task<bool> RestoreBestEffortAsync(
        IPersistentSecretStore secretStore,
        IReadOnlyList<CredentialValue> values)
    {
        var restored = true;
        for (var index = values.Count - 1; index >= 0; index--)
        {
            var value = values[index];
            try
            {
                if (value.Value == null)
                    await secretStore.DeleteAsync(value.Key).ConfigureAwait(false);
                else
                    await secretStore.SetAsync(value.Key, value.Value).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScraperCredentials] Rollback failed for {value.Key.Account}: {ex.Message}");
                restored = false;
            }
        }

        return restored;
    }

    private enum CredentialKind
    {
        ApiKey,
        Password,
        ClientSecret
    }

    internal sealed record CredentialValue(SecretKey Key, string? Value);

    public sealed class ScraperCredentialTransaction
    {
        private readonly IPersistentSecretStore _secretStore;
        private IReadOnlyList<CredentialValue>? _rollbackValues;

        internal ScraperCredentialTransaction(
            IPersistentSecretStore secretStore,
            IReadOnlyList<CredentialValue> rollbackValues)
        {
            _secretStore = secretStore;
            _rollbackValues = rollbackValues;
        }

        internal static ScraperCredentialTransaction Empty(IPersistentSecretStore secretStore)
            => new(secretStore, Array.Empty<CredentialValue>());

        public async Task<bool> RollbackAsync()
        {
            var values = Interlocked.Exchange(ref _rollbackValues, null);
            return values == null || await RestoreBestEffortAsync(_secretStore, values).ConfigureAwait(false);
        }

        public void Complete()
        {
            Interlocked.Exchange(ref _rollbackValues, null);
        }
    }
}
