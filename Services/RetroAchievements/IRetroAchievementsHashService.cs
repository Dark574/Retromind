using System.Threading;
using System.Threading.Tasks;

namespace Retromind.Services.RetroAchievements;

public interface IRetroAchievementsHashService
{
    Task<RetroAchievementsGameHash> CalculateAsync(
        string gameSystemId,
        string filePath,
        CancellationToken cancellationToken = default);
}
