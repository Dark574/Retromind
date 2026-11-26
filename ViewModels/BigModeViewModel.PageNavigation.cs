using System.Threading;
using Retromind.Helpers;

namespace Retromind.ViewModels;

public partial class BigModeViewModel
{
    private const int LibraryPageSize = BigModeScrollProfile.PageSize;
    private const int AchievementRowsPerPage = 3;
    private const int AchievementPageSize = AchievementColumnCount * AchievementRowsPerPage;

    private void OnGamepadPageStateChanged(int direction, bool pressed)
    {
        var generation = Volatile.Read(ref _navigationInputGeneration);
        DispatchGamepadAction(() =>
        {
            if (IsSearchOpen)
            {
                if (generation != _navigationInputGeneration || _disposed != 0 || _isLaunching) return;
                if (!pressed) { if (_gamepadRepeatPageDirection == direction) StopGamepadRepeatTimer(); return; }
                StopGamepadRepeatTimer();
                if (direction < 0) { Search!.Backspace(); StartGamepadRepeat(null, direction); }
                else Search!.ToggleScope();
                return;
            }
            if (_disposed != 0 || _isLaunching || _isCloseRequestInProgress || IsSystemMenuOpen || IsAlphabetNavigationOpen ||
                generation != _navigationInputGeneration) return;
            if (!pressed)
            {
                if (_gamepadRepeatPageDirection != direction) return;
                StopGamepadRepeatTimer();
                ResumePreviewAfterScroll();
                return;
            }
            if (_gamepadRepeatPageDirection == direction && _gamepadRepeatTimer?.IsEnabled == true) return;

            if (IsHomeActive || IsAchievementsOverlayOpen)
            {
                StopGamepadRepeatTimer();
                ResumePreviewAfterScroll();
                NavigatePage(direction);
                return;
            }

            SuspendPreviewForScroll();
            StartGamepadRepeat(null, direction);
            NavigatePage(direction);
        });
    }

    public void NavigatePage(int direction)
    {
        if (IsSearchOpen) { if (direction < 0) Search!.Backspace(); else if (direction > 0) Search!.ToggleScope(); return; }
        if (_disposed != 0 || _isCloseRequestInProgress || _isLaunching || IsSystemMenuOpen || IsAlphabetNavigationOpen || direction == 0)
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
        FlushNavigationChanges();
        var target = BigModePageNavigationHelper.GetTargetIndex(
            _navigation.Current.SelectedIndex, _navigation.Current.Entries.Count, LibraryPageSize, direction);
        return target >= 0 && target != _navigation.Current.SelectedIndex && _navigation.TrySelect(target);
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
