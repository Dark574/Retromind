using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Retromind.Models;

namespace Retromind.Services;

public class SettingsService
{
    private const string FileName = "app_settings.json";
    private string FilePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);

    public async Task SaveAsync(AppSettings settings)
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            using var stream = File.Create(FilePath);
            await JsonSerializer.SerializeAsync(stream, settings, options);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving settings: {ex.Message}");
        }
    }

    public async Task<AppSettings> LoadAsync()
    {
        if (!File.Exists(FilePath)) return new AppSettings();

        try
        {
            using var stream = File.OpenRead(FilePath);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }
}