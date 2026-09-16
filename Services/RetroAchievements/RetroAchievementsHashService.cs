using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Retromind.Services.RetroAchievements;

/// <summary>
/// Generates the system-specific game hash expected by RetroAchievements
/// through the pinned rcheevos rhash implementation.
/// </summary>
public sealed class RetroAchievementsHashService : IRetroAchievementsHashService
{
    internal const string ExpectedNativeVersion = "12.4.0";

    public async Task<RetroAchievementsGameHash> CalculateAsync(
        string gameSystemId,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameSystemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var normalizedSystemId = gameSystemId.Trim();
        if (!RetroAchievementsConsoleCatalog.TryGetConsoleId(normalizedSystemId, out var consoleId))
        {
            throw new NotSupportedException(
                $"The game system '{normalizedSystemId}' is not supported by the bundled RetroAchievements hash implementation.");
        }

        if (!File.Exists(filePath))
            throw new FileNotFoundException("The game file does not exist.", filePath);

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return await Task.Run(
                    () => CalculateCore(normalizedSystemId, consoleId, filePath, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DllNotFoundException or
                                   EntryPointNotFoundException or
                                   BadImageFormatException)
        {
            throw new RetroAchievementsHashException(
                "The bundled RetroAchievements hash library could not be loaded.",
                ex);
        }
    }

    internal static string GetNativeVersion()
    {
        var version = NativeMethods.GetVersion();
        return Marshal.PtrToStringUTF8(version) ?? string.Empty;
    }

    private static RetroAchievementsGameHash CalculateCore(
        string gameSystemId,
        uint consoleId,
        string filePath,
        CancellationToken cancellationToken)
    {
        var actualVersion = GetNativeVersion();
        if (!string.Equals(actualVersion, ExpectedNativeVersion, StringComparison.Ordinal))
        {
            throw new RetroAchievementsHashException(
                $"Unexpected RetroAchievements hash library version '{actualVersion}'. Expected '{ExpectedNativeVersion}'.");
        }

        var hashBuffer = new byte[33];
        if (NativeMethods.Generate(consoleId, filePath, hashBuffer) == 0)
        {
            throw new RetroAchievementsHashException(
                "RetroAchievements could not generate a game hash for this file and game system.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var terminator = Array.IndexOf(hashBuffer, (byte)0);
        var length = terminator >= 0 ? terminator : hashBuffer.Length;
        var hash = Encoding.ASCII.GetString(hashBuffer, 0, length);
        if (hash.Length != 32)
            throw new RetroAchievementsHashException("RetroAchievements returned an invalid game hash.");

        return new RetroAchievementsGameHash(gameSystemId, consoleId, hash);
    }

    private static class NativeMethods
    {
        private const string LibraryName = "retromind-rhash";

        [DllImport(LibraryName, EntryPoint = "retromind_rhash_generate", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Generate(
            uint consoleId,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            [Out] byte[] hash);

        [DllImport(LibraryName, EntryPoint = "retromind_rhash_version", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr GetVersion();
    }
}
