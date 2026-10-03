namespace Retromind.Models;

/// <summary>
/// User-configurable single-key bindings for BigMode. Values use Avalonia key
/// names as strings so the persisted settings remain readable and portable.
/// </summary>
public sealed class KeyboardBindingSettings
{
    public string NavigateUp { get; set; } = "Up";
    public string NavigateDown { get; set; } = "Down";
    public string NavigateLeft { get; set; } = "Left";
    public string NavigateRight { get; set; } = "Right";
    public string Select { get; set; } = "Enter";
    public string AlternateSelect { get; set; } = "Space";
    public string Back { get; set; } = "Back";
    public string ExitBigMode { get; set; } = "Escape";
    public string Details { get; set; } = "I";
    public string Home { get; set; } = "Home";
    public string SystemMenu { get; set; } = "F10";
    public string PreviousPage { get; set; } = "PageUp";
    public string NextPage { get; set; } = "PageDown";
}
