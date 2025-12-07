using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Retromind.Helpers;

public static class SecurityHelper
{
    // A fixed key allows portability (USB stick works on any PC).
    // NOTE: This protects against casual snooping (text editor), not dedicated hackers.
    // For true security, user-specific keys (DPAPI) would be needed, breaking portability.
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("Retromind_Secret_Key_2025!_Fixed"); // 32 bytes for AES-256
    private static readonly byte[] IV = Encoding.UTF8.GetBytes("Retromind_IV_123"); // 16 bytes for AES

    public static string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return plainText;

        try 
        {
            using var aes = Aes.Create();
            aes.Key = Key;
            aes.IV = IV;

            using var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
            using var ms = new MemoryStream();
            using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
            using (var sw = new StreamWriter(cs))
            {
                sw.Write(plainText);
            }
            return Convert.ToBase64String(ms.ToArray());
        }
        catch 
        {
            return string.Empty;
        }
    }

    public static string Decrypt(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText)) return cipherText;

        try
        {
            var buffer = Convert.FromBase64String(cipherText);
            using var aes = Aes.Create();
            aes.Key = Key;
            aes.IV = IV;

            using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
            using var ms = new MemoryStream(buffer);
            using (var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read))
            using (var sr = new StreamReader(cs))
            {
                return sr.ReadToEnd();
            }
        }
        catch
        {
            // Fail safe: return empty or original if decryption fails (e.g. was not encrypted)
            return string.Empty;
        }
    }
}