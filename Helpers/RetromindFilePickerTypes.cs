using Avalonia.Platform.Storage;

namespace Retromind.Helpers;

/// <summary>
/// Shared file-picker filters for media formats supported by Retromind's runtime players.
/// </summary>
public static class RetromindFilePickerTypes
{
    public static FilePickerFileType Audio => new("Audio")
    {
        Patterns = ["*.mp3", "*.ogg", "*.opus", "*.wav", "*.flac", "*.sid"]
    };
}
