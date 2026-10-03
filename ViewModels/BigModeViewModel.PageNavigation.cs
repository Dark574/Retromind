using Retromind.Helpers;

namespace Retromind.ViewModels;

public partial class BigModeViewModel
{
    private const int LibraryPageSize = 10;
    private const int AchievementRowsPerPage = 3;
    private const int AchievementPageSize = AchievementColumnCount * AchievementRowsPerPage;

    private void OnGamepadPreviousPage() => DispatchGamepadAction(() => NavigatePage(-1));
    private void OnGamepadNextPage() => DispatchGamepadAction(() => NavigatePage(1));

    public void NavigatePage(int direction)
    {
        if (_isLaunching || IsSystemMenuOpen || direction == 0)
            return;

        ResetAttractIdleTimer();

        var moved = IsAchievementsOverlayOpen
            ? NavigateAchievementPage(direction)
            : IsHomeActive
                ? NavigateHomePage(direction)
                : NavigateLibraryPage(direction);

        if (moved)
            PlaySound(_theme.Sounds.Navigate);
    }

    private bool NavigateLibraryPage(int direction)
    {
        if (IsGameListActive)
        {
            var targetIndex = BigModePageNavigationHelper.GetTargetIndex(
                SelectedItemIndex,
                Items.Count,
                LibraryPageSize,
                direction);
            if (targetIndex < 0 || targetIndex == SelectedItemIndex)
                return false;

            SelectedItemIndex = targetIndex;
            SelectedItem = Items[targetIndex];
            return true;
        }

        var categoryTargetIndex = BigModePageNavigationHelper.GetTargetIndex(
            SelectedCategoryIndex,
            CurrentCategories.Count,
            LibraryPageSize,
            direction);
        if (categoryTargetIndex < 0 || categoryTargetIndex == SelectedCategoryIndex)
            return false;

        SelectedCategoryIndex = categoryTargetIndex;
        SelectedCategory = CurrentCategories[categoryTargetIndex];
        return true;
    }

    private bool NavigateAchievementPage(int direction)
    {
        var achievements = RetroAchievementsProgress.AchievementItems;
        var currentIndex = -1;
        if (SelectedAchievement != null)
        {
            for (var index = 0; index < achievements.Count; index++)
            {
                if (!ReferenceEquals(achievements[index], SelectedAchievement))
                    continue;

                currentIndex = index;
                break;
            }
        }

        var targetIndex = BigModePageNavigationHelper.GetTargetIndex(
            currentIndex,
            achievements.Count,
            AchievementPageSize,
            direction);
        if (targetIndex < 0 || targetIndex == currentIndex)
            return false;

        SelectedAchievement = achievements[targetIndex];
        return true;
    }

    private bool NavigateHomePage(int direction)
    {
        var section = SelectedHomeSection;
        if (section == null)
            return false;

        if (section.IsPaged)
            return section.MovePage(direction, selectLastEntry: direction < 0);

        var currentIndex = section.SelectedEntry == null
            ? -1
            : section.Entries.IndexOf(section.SelectedEntry);
        var targetIndex = BigModePageNavigationHelper.GetTargetIndex(
            currentIndex,
            section.Entries.Count,
            LibraryPageSize,
            direction);
        if (targetIndex < 0 || targetIndex == currentIndex)
            return false;

        section.SelectedEntry = section.Entries[targetIndex];
        return true;
    }
}
