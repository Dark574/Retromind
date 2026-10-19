using System.Threading;
using System.Threading.Tasks;

namespace Retromind.Services.Achievements;

public interface IStoreAchievementImageCacheService
{
    Task<string?> GetImagePathAsync(
        StoreAchievementProvider provider,
        string? imageUrl,
        CancellationToken cancellationToken = default);
}
