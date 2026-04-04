using Retromind.Models;

namespace Retromind.Helpers;

public static class ParentalControlHelper
{
    public static bool IsFilterActive(AppSettings settings)
    {
        if (settings == null)
            return false;

        return !string.IsNullOrWhiteSpace(settings.ParentalLockPasswordEncrypted) &&
               !settings.ParentalLockUnlocked;
    }

    public static bool CanShowItem(MediaItem item, bool filterActive)
    {
        if (item == null)
            return false;

        return !filterActive || !item.IsProtected;
    }
}
