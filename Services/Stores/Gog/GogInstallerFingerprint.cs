using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Retromind.Services.Stores.Gog;

internal static class GogInstallerFingerprint
{
    internal static string NormalizeVersion(string? version)
        => string.IsNullOrWhiteSpace(version) ? string.Empty : version.Trim();

    internal static string BuildSignature(GogInstallerPackage package)
    {
        var builder = new StringBuilder();
        foreach (var file in package.Files
                     .OrderBy(file => file.FileName ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(file.FileName?.Trim() ?? string.Empty);
            builder.Append('|');
            builder.Append(file.Size?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            builder.Append('\n');
        }

        var payload = builder.ToString();
        if (payload.Length == 0)
            return string.Empty;

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hashBytes);
    }
}
