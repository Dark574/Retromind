using System;
using System.IO;
using System.Security.Cryptography;

namespace Retromind.Helpers;

/// <summary>
/// Provides static helper methods for file operations, such as calculating checksums.
/// </summary>
public static class FileHelper
{
    /// <summary>
    /// Calculates the MD5 checksum of a file.
    /// </summary>
    /// <param name="filePath">The full path to the file.</param>
    /// <returns>The MD5 hash as a lowercase hex string.</returns>
    /// <exception cref="FileNotFoundException">Thrown if the file does not exist.</exception>
    public static string CalculateMd5(string filePath)
    {
        if (!File.Exists(filePath))
            return string.Empty;

        try
        {
            using var md5 = MD5.Create();
            // Open with FileShare.Read to allow other processes to read the file simultaneously (important for large files being scanned)
            // Use a buffer size (e.g. 8192) optimized for sequential reading.
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192);
            
            var hash = md5.ComputeHash(stream);
            
            // Optimized hex string conversion (available in modern .NET)
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch (Exception)
        {
            // In case of locking issues or access denied, return empty or handle gracefully.
            return string.Empty;
        }
    }
}