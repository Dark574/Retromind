using System;

namespace Retromind.Models;

/// <summary>
/// Enum representing types of media assets used in the application.
/// </summary>
public enum AssetType
{
    Unknown = 0,
    Cover,
    Wallpaper,
    Logo,
    Video,
    Marquee,
    Music,
    Banner
}

/// <summary>
/// Represents a media asset with type and path information.
/// Uses relative paths for portability across systems (e.g., Linux).
/// </summary>
public class MediaAsset
{
    /// <summary>
    /// Unique identifier for the asset. Generated automatically if not set.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    
    /// <summary>
    /// Type of the media asset.
    /// </summary>
    public AssetType Type { get; set; }
    
    private string? _absolutePath; // Cache for computed absolute path
    
    /// <summary>
    /// Gets the absolute path by combining the app's base directory with the relative path.
    /// Cached for performance in large collections.
    /// </summary>
    public string AbsolutePath
    {
        get
        {
            if (_absolutePath == null)
            {
                _absolutePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, RelativePath);
            }
            return _absolutePath;
        }
    }
    
    /// <summary>
    /// Stores the RELATIVE path (e.g., "Games/SNES/Cover/Mario_Cover_01.jpg").
    /// </summary>
    public string RelativePath { get; set; } = string.Empty;
}