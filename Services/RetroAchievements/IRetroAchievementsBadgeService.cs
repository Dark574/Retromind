using System.Threading;
using System.Threading.Tasks;

namespace Retromind.Services.RetroAchievements;

public interface IRetroAchievementsBadgeService
{
    Task<string?> GetBadgePathAsync(
        string? badgeName,
        bool isUnlocked,
        CancellationToken cancellationToken = default);
}
