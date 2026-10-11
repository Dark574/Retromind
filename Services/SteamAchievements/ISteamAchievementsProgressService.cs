using System.Threading;
using System.Threading.Tasks;

namespace Retromind.Services.SteamAchievements;

public interface ISteamAchievementsProgressService
{
    Task<SteamProgressSnapshot> GetProgressAsync(
        uint appId,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);
}
