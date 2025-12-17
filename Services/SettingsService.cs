using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
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
    
    /// <summary>
    /// Saves the settings asynchronously.
    /// Uses a temporary file strategy to prevent data corruption during crashes.
    /// </summary>
    public async Task SaveAsync(AppSettings settings)
    {
        // 1. Encrypt sensitive data before serializing
        ProtectSensitiveData(settings);
        
        try
        {
            #if DEBUG
                var options = new JsonSerializerOptions { WriteIndented = true };
            #else
                var options = new JsonSerializerOptions { WriteIndented = false };
            #endif

            // 1) Write to temp first
            using (var stream = File.Create(TempPath))
            {
                await JsonSerializer.SerializeAsync(stream, settings, options);
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
                    // best effort: backup must not block saving
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
    }

    /// <summary>
    /// Loads the settings from disk.
    /// Creates a backup if the existing file is corrupted.
    /// </summary>
    public async Task<AppSettings> LoadAsync()
    {
        if (!File.Exists(FilePath)) return new AppSettings();

        try
        {
            using var stream = File.OpenRead(FilePath);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream) ?? new AppSettings();

            // 2. Decrypt sensitive data after loading
            UnprotectSensitiveData(settings);
            
            return settings;
        }
        catch (JsonException jsonEx)
        {
            Debug.WriteLine($"[SettingsService] Settings file corrupted: {jsonEx.Message}");
            BackupCorruptedFile();
            return new AppSettings(); // Return defaults
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsService] Error loading settings: {ex.Message}");
            return new AppSettings();
        }
    }

    private void BackupCorruptedFile()
    {
        try
        {
            var backupPath = FilePath + $".bak_{DateTime.Now:yyyyMMdd_HHmmss}";
            if (File.Exists(FilePath))
            {
                File.Copy(FilePath, backupPath, overwrite: true);
                Debug.WriteLine($"[SettingsService] Corrupted settings backed up to: {backupPath}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsService] Failed to backup corrupted settings: {ex.Message}");
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