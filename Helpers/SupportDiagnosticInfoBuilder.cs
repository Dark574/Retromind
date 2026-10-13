using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Retromind.Helpers;

public static class SupportDiagnosticInfoBuilder
{
    public static string Build(bool isWaylandRequested, bool portableHomeEnabled)
    {
        var assembly = typeof(SupportDiagnosticInfoBuilder).Assembly;
        var version = assembly
                          .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                          ?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString()
                      ?? "unknown";

        return Build(
            version,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture,
            RuntimeInformation.ProcessArchitecture,
            Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"),
            Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
            isWaylandRequested,
            IsAppImageEnvironment(),
            portableHomeEnabled);
    }

    internal static string Build(
        string version,
        string runtimeDescription,
        string operatingSystemDescription,
        Architecture operatingSystemArchitecture,
        Architecture processArchitecture,
        string? desktop,
        string? sessionType,
        bool isWaylandRequested,
        bool isAppImage,
        bool portableHomeEnabled)
    {
        var result = new StringBuilder()
            .AppendLine("Retromind support information")
            .AppendLine($"Retromind: {Sanitize(version)}")
            .AppendLine($"Runtime: {Sanitize(runtimeDescription)}")
            .AppendLine($"Operating system: {Sanitize(operatingSystemDescription)}")
            .AppendLine($"OS architecture: {operatingSystemArchitecture}")
            .AppendLine($"Process architecture: {processArchitecture}")
            .AppendLine($"Desktop: {ValueOrUnavailable(desktop)}")
            .AppendLine($"Session type: {ValueOrUnavailable(sessionType)}")
            .AppendLine($"Avalonia backend: {(isWaylandRequested ? "Wayland" : "X11")}")
            .AppendLine($"Packaging: {(isAppImage ? "AppImage" : "Other")}")
            .Append($"Portable AppImage HOME: {(portableHomeEnabled ? "enabled" : "disabled")}");

        return result.ToString();
    }

    private static bool IsAppImageEnvironment() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APPIMAGE")) ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APPDIR"));

    private static string ValueOrUnavailable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "unavailable" : Sanitize(value);

    private static string Sanitize(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
