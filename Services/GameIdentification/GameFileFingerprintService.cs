using System;
using System.Buffers;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Retromind.Services.GameIdentification;

/// <summary>
/// Calculates common file checksums in one sequential, cancelable read.
/// </summary>
public sealed class GameFileFingerprintService : IGameFileFingerprintService
{
    private const int BufferSize = 128 * 1024;

    public async Task<GameFileFingerprint> CalculateAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        cancellationToken.ThrowIfCancellationRequested();

        var initialFile = new FileInfo(filePath);
        if (!initialFile.Exists)
            throw new FileNotFoundException("The game file does not exist.", filePath);

        var initialLength = initialFile.Length;
        var initialLastWriteTimeUtc = initialFile.LastWriteTimeUtc;
        var crc32 = new Crc32Accumulator();
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long bytesRead = 0;

        try
        {
            await using var stream = new FileStream(filePath, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = BufferSize,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });

            while (true)
            {
                var count = await stream
                    .ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)
                    .ConfigureAwait(false);
                if (count == 0)
                    break;

                var data = buffer.AsSpan(0, count);
                crc32.Append(data);
                md5.AppendData(data);
                sha1.AppendData(data);
                bytesRead += count;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var completedFile = new FileInfo(filePath);
        completedFile.Refresh();
        if (!completedFile.Exists ||
            bytesRead != initialLength ||
            completedFile.Length != initialLength ||
            completedFile.LastWriteTimeUtc != initialLastWriteTimeUtc)
        {
            throw new IOException("The game file changed while its fingerprint was being calculated.");
        }

        return new GameFileFingerprint(
            initialLength,
            initialLastWriteTimeUtc,
            crc32.GetCurrentHash().ToString("x8", CultureInfo.InvariantCulture),
            Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant(),
            Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant());
    }

    private sealed class Crc32Accumulator
    {
        private const uint InitialValue = uint.MaxValue;
        private static readonly uint[] Table = CreateTable();
        private uint _value = InitialValue;

        public void Append(ReadOnlySpan<byte> data)
        {
            foreach (var value in data)
                _value = Table[(byte)(_value ^ value)] ^ (_value >> 8);
        }

        public uint GetCurrentHash() => ~_value;

        private static uint[] CreateTable()
        {
            const uint polynomial = 0xedb88320;
            var table = new uint[256];

            for (var index = 0; index < table.Length; index++)
            {
                var value = (uint)index;
                for (var bit = 0; bit < 8; bit++)
                    value = (value & 1) != 0 ? (value >> 1) ^ polynomial : value >> 1;

                table[index] = value;
            }

            return table;
        }
    }
}
