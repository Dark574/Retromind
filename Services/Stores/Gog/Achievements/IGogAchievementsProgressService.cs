using System.Threading;
using System.Threading.Tasks;

namespace Retromind.Services.Stores.Gog.Achievements;

public interface IGogAchievementsProgressService
{
    Task<GogProgressSnapshot> GetProgressAsync(
        string gameId,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);
}
