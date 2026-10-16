using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.Services.Stores.Security;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services;

public sealed class ScraperCredentialServiceTests
{
    [Fact]
    public async Task InitializeAsync_MigratesLegacyValuesAndSanitizesPrimaryAndBackup()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var settingsService = new SettingsService();
        var profile = new ScraperConfig
        {
            Id = "profile-1",
            Type = ScraperType.IGDB,
            EncryptedApiKey = SecurityHelper.Encrypt("api-secret"),
            EncryptedPassword = SecurityHelper.Encrypt("password-secret"),
            EncryptedClientSecret = SecurityHelper.Encrypt("client-secret")
        };
        var persisted = new AppSettings { Scrapers = [profile] };
        var legacyJson = settingsService.Serialize(persisted);
        await File.WriteAllTextAsync(temp.GetPath("app_settings.json"), legacyJson);
        await File.WriteAllTextAsync(temp.GetPath("app_settings.json.bak"), legacyJson);

        var loaded = await settingsService.LoadAsync();
        var store = new TestPersistentSecretStore();
        var service = new ScraperCredentialService(store);

        await service.InitializeAsync(loaded, settingsService);

        var migrated = Assert.Single(loaded.Scrapers);
        Assert.Equal("api-secret", migrated.ApiKey);
        Assert.Equal("password-secret", migrated.Password);
        Assert.Equal("client-secret", migrated.ClientSecret);
        Assert.Null(migrated.EncryptedApiKey);
        Assert.Null(migrated.EncryptedPassword);
        Assert.Null(migrated.EncryptedClientSecret);
        Assert.Equal("api-secret", await store.GetAsync(new SecretKey("retromind:scraper", "profile-1:api-key")));
        Assert.Equal("password-secret", await store.GetAsync(new SecretKey("retromind:scraper", "profile-1:password")));
        Assert.Equal("client-secret", await store.GetAsync(new SecretKey("retromind:scraper", "profile-1:client-secret")));

        foreach (var path in new[]
                 {
                     temp.GetPath("app_settings.json"),
                     temp.GetPath("app_settings.json.bak")
                 })
        {
            var json = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("EncryptedApiKey", json, StringComparison.Ordinal);
            Assert.DoesNotContain("EncryptedPassword", json, StringComparison.Ordinal);
            Assert.DoesNotContain("EncryptedClientSecret", json, StringComparison.Ordinal);
            Assert.DoesNotContain("api-secret", json, StringComparison.Ordinal);
            Assert.DoesNotContain("password-secret", json, StringComparison.Ordinal);
            Assert.DoesNotContain("client-secret", json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task InitializeAsync_PreservesLegacyValueWhenPersistentWriteFails()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var settingsService = new SettingsService();
        var cipherText = SecurityHelper.Encrypt("keep-me");
        var settings = new AppSettings
        {
            Scrapers =
            [
                new ScraperConfig
                {
                    Id = "profile-1",
                    EncryptedApiKey = cipherText
                }
            ]
        };
        var legacyJson = settingsService.Serialize(settings);
        await File.WriteAllTextAsync(temp.GetPath("app_settings.json"), legacyJson);
        settings = await settingsService.LoadAsync();
        var store = new TestPersistentSecretStore { FailWrites = true };

        await new ScraperCredentialService(store).InitializeAsync(settings, settingsService);

        var profile = Assert.Single(settings.Scrapers);
        Assert.Equal("keep-me", profile.ApiKey);
        Assert.Equal(cipherText, profile.EncryptedApiKey);
        Assert.Equal(legacyJson, await File.ReadAllTextAsync(temp.GetPath("app_settings.json")));
    }

    [Fact]
    public async Task InitializeAsync_PrefersExistingKeyringValueAndRemovesLegacyValue()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var settingsService = new SettingsService();
        var settings = new AppSettings
        {
            Scrapers =
            [
                new ScraperConfig
                {
                    Id = "profile-1",
                    EncryptedApiKey = SecurityHelper.Encrypt("legacy"),
                    EncryptedPassword = string.Empty
                }
            ]
        };
        await settingsService.SaveAsync(settings);
        settings = await settingsService.LoadAsync();
        var store = new TestPersistentSecretStore();
        await store.SetAsync(new SecretKey("retromind:scraper", "profile-1:api-key"), "keyring");

        await new ScraperCredentialService(store).InitializeAsync(settings, settingsService);

        var profile = Assert.Single(settings.Scrapers);
        Assert.Equal("keyring", profile.ApiKey);
        Assert.Null(profile.EncryptedApiKey);
        Assert.Null(profile.EncryptedPassword);
    }

    [Fact]
    public async Task ApplyChangesAsync_RollbackRestoresChangedAndRemovedCredentials()
    {
        var store = new TestPersistentSecretStore();
        var service = new ScraperCredentialService(store);
        var oldKey = new SecretKey("retromind:scraper", "old-profile:api-key");
        await store.SetAsync(oldKey, "old-value");
        var previous = new[]
        {
            new ScraperConfig { Id = "old-profile", ApiKey = "old-value" }
        };
        var next = new[]
        {
            new ScraperConfig { Id = "new-profile", Password = "new-password" }
        };

        var transaction = await service.ApplyChangesAsync(previous, next);

        Assert.Null(await store.GetAsync(oldKey));
        Assert.Equal(
            "new-password",
            await store.GetAsync(new SecretKey("retromind:scraper", "new-profile:password")));

        Assert.True(await transaction.RollbackAsync());
        Assert.Equal("old-value", await store.GetAsync(oldKey));
        Assert.Null(await store.GetAsync(new SecretKey("retromind:scraper", "new-profile:password")));
    }

    [Fact]
    public void Serialize_DoesNotCreateLegacyCiphertextFromRuntimeCredentials()
    {
        var settings = new AppSettings
        {
            Scrapers =
            [
                new ScraperConfig
                {
                    Id = "profile-1",
                    ApiKey = "api-secret",
                    Password = "password-secret",
                    ClientSecret = "client-secret"
                }
            ]
        };

        var json = new SettingsService().Serialize(settings);

        Assert.DoesNotContain("EncryptedApiKey", json, StringComparison.Ordinal);
        Assert.DoesNotContain("EncryptedPassword", json, StringComparison.Ordinal);
        Assert.DoesNotContain("EncryptedClientSecret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("api-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("password-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("client-secret", json, StringComparison.Ordinal);
    }

    private static EnvironmentVariableScope UseDataRoot(string rootPath)
        => new(
            ("APPIMAGE", Path.Combine(rootPath, "Retromind.AppImage")),
            ("APPDIR", null));

    private sealed class TestPersistentSecretStore : IPersistentSecretStore
    {
        private readonly Dictionary<SecretKey, string> _values = new();

        public bool FailWrites { get; init; }

        public Task<bool> IsAvailableAsync(CancellationToken ct = default)
            => Task.FromResult(true);

        public Task SetAsync(SecretKey key, string secret, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (FailWrites)
                throw new InvalidOperationException("Simulated keyring write failure.");

            _values[key] = secret;
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(SecretKey key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_values.GetValueOrDefault(key));
        }

        public Task DeleteAsync(SecretKey key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (FailWrites)
                throw new InvalidOperationException("Simulated keyring write failure.");

            _values.Remove(key);
            return Task.CompletedTask;
        }
    }
}
