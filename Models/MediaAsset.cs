using System;

namespace Retromind.Models;

public enum AssetType
{
    Unknown = 0,
    Cover,
    Wallpaper,
    Logo,
    Video,
    Marquee,
    Music
}

public class MediaAsset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    public AssetType Type { get; set; }
    
    public string AbsolutePath => System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, RelativePath);
    
    // Speichert den RELATIVEN Pfad (z.B. "Games/SNES/Cover/Mario_Cover_01.jpg")
    public string RelativePath { get; set; } = string.Empty;
}