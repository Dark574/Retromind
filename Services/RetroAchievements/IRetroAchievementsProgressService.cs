using System.Threading;
using System.Threading.Tasks;

namespace Retromind.Services.RetroAchievements;

public interface IRetroAchievementsProgressService
{
    Task<RetroAchievementsProgressSnapshot> GetProgressAsync(
        int gameId,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);
}
