using System.Threading;
using System.Threading.Tasks;

namespace Retromind.Services.RetroAchievements;

public interface IRetroAchievementsGameIdentificationService
{
    Task<RetroAchievementsIdentificationResult> IdentifyAsync(
        string gameSystemId,
        string filePath,
        CancellationToken cancellationToken = default);

    Task<RetroAchievementsIdentificationResult> IdentifyAsync(
        string gameSystemId,
        string filePath,
        string apiKey,
        CancellationToken cancellationToken = default);
}
