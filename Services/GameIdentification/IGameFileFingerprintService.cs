using System.Threading;
using System.Threading.Tasks;

namespace Retromind.Services.GameIdentification;

public interface IGameFileFingerprintService
{
    Task<GameFileFingerprint> CalculateAsync(
        string filePath,
        CancellationToken cancellationToken = default);
}
