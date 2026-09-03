using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services;

/// <summary>
/// Service responsible for persisting application settings.
/// Configured for portable usage: Settings are stored directly in the application directory.
/// </summary>
public class SettingsService
{
    private const string FileName = "app_settings.json";

    // Ensure the application has write permissions to its own folder!
    private string SettingsFolder => AppPaths.DataRoot;
    private string FilePath => Path.Combine(SettingsFolder, FileName);

    // keep a stable backup of the last known-good settings
    private string BackupPath => FilePath + ".bak";
    private string TempPath => FilePath + ".tmp";
    
    // Serialize settings IO to avoid concurrent temp/backup/replace races.
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private SettingsLoadException? _loadFailure;

    public SettingsLoadException? LoadFailure => Volatile.Read(ref _loadFailure);

    public bool HasLoadFailure => LoadFailure != null;
    
    /// <summary>
    /// Saves the settings asynchronously.
    /// Uses a temporary file strategy to prevent data corruption during crashes.
    /// </summary>
    public async Task SaveAsync(AppSettings settings)
    {
        ThrowIfLoadFailed();
        var json = Serialize(settings);
        await SaveJsonAsync(json).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads the settings from disk.
    /// Restores a valid backup when the primary file cannot be loaded.
    /// </summary>
    /// <exception cref="SettingsLoadException">
    /// Thrown when persisted settings exist but neither the primary file nor its backup can be loaded.
    /// Further writes are blocked for this service instance after this failure.
    /// </exception>
    public async Task<AppSettings> LoadAsync()
    {
        ThrowIfLoadFailed();
        await _ioGate.WaitAsync().ConfigureAwait(false);
        Exception? primaryError = null;
        Exception? backupError = null;
        try
        {
            ThrowIfLoadFailed();

            // Ensure settings directory exists so later SaveAsync won't fail on missing folder.
            Directory.CreateDirectory(SettingsFolder);

            // Cleanup stale temp file (e.g. after a crash during SaveAsync).
            try
            {
                if (File.Exists(TempPath))
                    File.Delete(TempPath);
            }
            catch
            {
                // best effort
            }

            var mainFileExists = PathEntryExists(FilePath);
            if (mainFileExists)
            {
                try
                {
                    return await LoadFromFileAsync(FilePath).ConfigureAwait(false);
                }
                catch (JsonException ex)
                {
                    primaryError = ex;
                    Debug.WriteLine($"[SettingsService] Settings file corrupted: {ex.Message}");

                    // Quarantine corrupt JSON for inspection. Other read failures stay untouched.
                    try
                    {
                        var corruptPath = FilePath + $".corrupt_{DateTime.Now:yyyyMMdd_HHmmss}";
                        File.Move(FilePath, corruptPath, overwrite: true);
                        Debug.WriteLine($"[SettingsService] Corrupted settings moved to: {corruptPath}");
                    }
                    catch
                    {
                        // best effort
                    }
                }
                catch (Exception ex)
                {
                    primaryError = ex;
                    Debug.WriteLine($"[SettingsService] Error loading settings: {ex.Message}");
                }
            }

            var backupFileExists = PathEntryExists(BackupPath);
            if (backupFileExists)
            {
                Debug.WriteLine("[SettingsService] Attempting to restore settings from .bak ...");
                try
                {
                    var restored = await LoadFromFileAsync(BackupPath).ConfigureAwait(false);

                    // Best effort: restore backup to the primary path so the next start is clean.
                    try
                    {
                        File.Copy(BackupPath, FilePath, overwrite: true);
                    }
                    catch
                    {
                        // best effort
                    }

                    return restored;
                }
                catch (Exception ex)
                {
                    backupError = ex;
                    Debug.WriteLine($"[SettingsService] Failed to load backup: {ex.Message}");
                }
            }

            // No persisted settings means a legitimate first start. Existing but unreadable
            // or invalid settings must never be treated as authoritative defaults.
            if (!mainFileExists && !backupFileExists)
                return new AppSettings();

            throw RecordLoadFailure(primaryError, backupError);
        }
        catch (SettingsLoadException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw RecordLoadFailure(primaryError ?? ex, backupError);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    private async Task<AppSettings> LoadFromFileAsync(string path)
    {
        using var stream = File.OpenRead(path);
        var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream).ConfigureAwait(false)
                       ?? throw new JsonException($"Settings file '{path}' contains null instead of settings.");

        UnprotectSensitiveData(settings);
        return settings;
    }

    /// <summary>
    /// Serializes the settings using the same options as SaveAsync.
    /// Call this on the UI thread to avoid cross-thread collection access.
    /// </summary>
    public string Serialize(AppSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));

        // Encrypt sensitive data before serializing (in-place).
        ProtectSensitiveData(settings);

        var options = CreateSerializerOptions();
        return JsonSerializer.Serialize(settings, options);
    }

    /// <summary>
    /// Saves a pre-serialized JSON snapshot to disk asynchronously using
    /// the same atomic write strategy as SaveAsync.
    /// </summary>
    public async Task SaveJsonAsync(string json)
    {
        if (json == null) throw new ArgumentNullException(nameof(json));
        ThrowIfLoadFailed();

        await _ioGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfLoadFailed();

            // Ensure settings directory exists (portable installs may start from a fresh folder).
            Directory.CreateDirectory(SettingsFolder);

            // 1) Write to temp first
            await File.WriteAllTextAsync(TempPath, json).ConfigureAwait(false);

            // 2) Backup current file (best effort)
            if (File.Exists(FilePath))
            {
                try
                {
                    File.Copy(FilePath, BackupPath, overwrite: true);
                }
                catch
                {
                    // Best effort: backup must not block saving.
                }
            }

            // 3) Atomic replace (no "delete then move" gap)
            File.Move(TempPath, FilePath, overwrite: true);
        }
        catch (SettingsLoadException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (ex is UnauthorizedAccessException)
            {
                Debug.WriteLine("[SettingsService] Write permission denied. Move Retromind to a user-writable folder.");
            }
            else
            {
                Debug.WriteLine($"[SettingsService] Critical error saving settings: {ex.Message}");
            }

            // Best effort cleanup of temp
            try
            {
                if (File.Exists(TempPath))
                    File.Delete(TempPath);
            }
            catch
            {
                // ignore
            }

            // Optional: if the main file is missing but we have a backup, restore it
            try
            {
                if (!File.Exists(FilePath) && File.Exists(BackupPath))
                    File.Copy(BackupPath, FilePath, overwrite: true);
            }
            catch
            {
                // ignore
            }

            // Do not report a failed write as success. UI and orchestration layers
            // decide how to notify the user or whether to retry.
            throw;
        }
        finally
        {
            _ioGate.Release();
        }
    }

    private SettingsLoadException RecordLoadFailure(Exception? primaryError, Exception? backupError)
    {
        var failure = new SettingsLoadException(primaryError, backupError);
        return Interlocked.CompareExchange(ref _loadFailure, failure, null) ?? failure;
    }

    private void ThrowIfLoadFailed()
    {
        if (LoadFailure is { } failure)
            throw failure;
    }

    private static bool PathEntryExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }
    
    private void ProtectSensitiveData(AppSettings settings)
    {
        foreach (var scraper in settings.Scrapers)
        {
            scraper.EncryptedApiKey = SecurityHelper.Encrypt(scraper.ApiKey ?? "");
            scraper.EncryptedPassword = SecurityHelper.Encrypt(scraper.Password ?? "");
            scraper.EncryptedClientSecret = SecurityHelper.Encrypt(scraper.ClientSecret ?? "");
        }
    }

    private void UnprotectSensitiveData(AppSettings settings)
    {
        foreach (var scraper in settings.Scrapers)
        {
            scraper.ApiKey = SecurityHelper.Decrypt(scraper.EncryptedApiKey ?? "");
            scraper.Password = SecurityHelper.Decrypt(scraper.EncryptedPassword ?? "");
            scraper.ClientSecret = SecurityHelper.Decrypt(scraper.EncryptedClientSecret ?? "");
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        return new JsonSerializerOptions { WriteIndented = true };
    }
}
