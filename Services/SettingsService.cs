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
    
    /// <summary>
    /// Saves the settings asynchronously.
    /// Uses a temporary file strategy to prevent data corruption during crashes.
    /// </summary>
    public async Task SaveAsync(AppSettings settings)
    {
        await _ioGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Ensure settings directory exists (portable installs may start from a fresh folder).
            Directory.CreateDirectory(SettingsFolder);

            // Encrypt sensitive data before serializing (in-place).
            ProtectSensitiveData(settings);

            #if DEBUG
            var options = new JsonSerializerOptions { WriteIndented = true };
            #else
            var options = new JsonSerializerOptions { WriteIndented = false };
            #endif

            // 1) Write to temp first
            await using (var stream = File.Create(TempPath))
            {
                await JsonSerializer.SerializeAsync(stream, settings, options).ConfigureAwait(false);
            }

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
        catch (UnauthorizedAccessException)
        {
            Debug.WriteLine("[SettingsService] Write permission denied. Run as Admin or move app to a user-writable folder.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsService] Critical error saving settings: {ex.Message}");

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
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>
    /// Loads the settings from disk.
    /// Creates a backup if the existing file is corrupted.
    /// </summary>
    public async Task<AppSettings> LoadAsync()
    {
        await _ioGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Ensure settings directory exists so later SaveAsync won't fail on missing folder.
            Directory.CreateDirectory(SettingsFolder);

            // 0) Nothing there -> defaults
            if (!File.Exists(FilePath))
            {
                // If only backup exists (rare), try to restore it.
                if (File.Exists(BackupPath))
                {
                    try
                    {
                        File.Copy(BackupPath, FilePath, overwrite: true);
                        return await LoadFromFileAsync(FilePath).ConfigureAwait(false);
                    }
                    catch
                    {
                        // fall through to defaults
                    }
                }

                return new AppSettings();
            }

            // 1) Try main file
            try
            {
                var settings = await LoadFromFileAsync(FilePath).ConfigureAwait(false);
                if (settings != null) return settings;

                return new AppSettings();
            }
            catch (JsonException jsonEx)
            {
                Debug.WriteLine($"[SettingsService] Settings file corrupted: {jsonEx.Message}");

                // Quarantine corrupt file for inspection
                try
                {
                    var corruptPath = FilePath + $".corrupt_{DateTime.Now:yyyyMMdd_HHmmss}";
                    File.Move(FilePath, corruptPath, overwrite: true);
                    Debug.WriteLine($"[SettingsService] Corrupted settings moved to: {corruptPath}");
                }
                catch
                {
                    // ignore
                }

                // 2) Try backup
                if (File.Exists(BackupPath))
                {
                    Debug.WriteLine("[SettingsService] Attempting to restore settings from .bak ...");
                    var restored = await LoadFromFileAsync(BackupPath).ConfigureAwait(false);
                    if (restored != null)
                    {
                        // Best effort: restore backup to main file so next start is clean
                        try
                        {
                            File.Copy(BackupPath, FilePath, overwrite: true);
                        }
                        catch
                        {
                            // ignore
                        }

                        return restored;
                    }
                }

                return new AppSettings();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SettingsService] Error loading settings: {ex.Message}");

                // As a last resort, try backup
                if (File.Exists(BackupPath))
                {
                    var restored = await LoadFromFileAsync(BackupPath).ConfigureAwait(false);
                    if (restored != null) return restored;
                }

                return new AppSettings();
            }
            finally
            {
                // Cleanup stale temp file (best effort)
                try
                {
                    if (File.Exists(TempPath))
                        File.Delete(TempPath);
                }
                catch
                {
                    // ignore
                }
            }
        }
        finally
        {
            _ioGate.Release();
        }
    }

    private async Task<AppSettings?> LoadFromFileAsync(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream).ConfigureAwait(false);
            settings ??= new AppSettings();

            UnprotectSensitiveData(settings);
            return settings;
        }
        catch (JsonException)
        {
            return null;
        }
        catch
        {
            return null;
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
}