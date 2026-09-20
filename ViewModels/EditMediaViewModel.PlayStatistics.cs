using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Retromind.ViewModels;

public partial class EditMediaViewModel
{
    private const decimal MaximumPlayTimeHours = 256204778m;

    private bool _isLoadingPlayStatistics;
    private bool _playTimeWasEdited;
    private bool _lastPlayedWasEdited;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayedStatusText))]
    private decimal _playCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayedStatusText))]
    private decimal _playTimeHours;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayedStatusText))]
    private decimal _playTimeMinutes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayedStatusText))]
    private decimal _playTimeSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastPlayedDate))]
    [NotifyPropertyChangedFor(nameof(PlayedStatusText))]
    private DateTimeOffset? _lastPlayedDate;

    [ObservableProperty]
    private TimeSpan? _lastPlayedTime;

    public string PlayStatisticsTitle => T("EditMedia.PlayStatisticsTitle", "Play statistics");
    public string PlayStatisticsHint => T(
        "EditMedia.PlayStatisticsHint",
        "These values are normally updated automatically. You can correct imported or inaccurate statistics here.");
    public string PlayedStatusLabel => T("EditMedia.PlayedStatus", "Played");
    public string PlayedStatusText => HasStagedPlayEvidence
        ? T("EditMedia.PlayedYes", "Yes")
        : T("EditMedia.PlayedNo", "No");
    public string PlayCountLabel => T("EditMedia.PlayCount", "Launch count");
    public string PlayTimeHoursLabel => T("EditMedia.PlayTimeHours", "Hours");
    public string PlayTimeMinutesLabel => T("EditMedia.PlayTimeMinutes", "Minutes");
    public string PlayTimeSecondsLabel => T("EditMedia.PlayTimeSeconds", "Seconds");
    public string LastPlayedLabel => T("EditMedia.LastPlayed", "Last played");
    public string ClearLastPlayedText => T("EditMedia.ClearLastPlayed", "Clear");
    public string ResetPlayStatisticsText => T("EditMedia.ResetPlayStatistics", "Reset statistics");

    public bool HasLastPlayedDate => LastPlayedDate.HasValue;

    private bool HasStagedPlayEvidence =>
        PlayCount > 0 ||
        PlayTimeHours > 0 ||
        PlayTimeMinutes > 0 ||
        PlayTimeSeconds > 0 ||
        LastPlayedDate.HasValue;

    private void LoadPlayStatistics()
    {
        _isLoadingPlayStatistics = true;
        try
        {
            PlayCount = Math.Max(0, _originalItem.PlayCount);

            var totalPlayTime = _originalItem.TotalPlayTime < TimeSpan.Zero
                ? TimeSpan.Zero
                : _originalItem.TotalPlayTime;
            PlayTimeHours = Math.Truncate((decimal)totalPlayTime.TotalHours);
            PlayTimeMinutes = totalPlayTime.Minutes;
            PlayTimeSeconds = totalPlayTime.Seconds;

            if (_originalItem.LastPlayed is { } lastPlayed)
            {
                var localLastPlayed = lastPlayed.Kind == DateTimeKind.Utc
                    ? lastPlayed.ToLocalTime()
                    : lastPlayed;
                LastPlayedDate = new DateTimeOffset(localLastPlayed);
                LastPlayedTime = localLastPlayed.TimeOfDay;
            }
            else
            {
                LastPlayedDate = null;
                LastPlayedTime = null;
            }
        }
        finally
        {
            _isLoadingPlayStatistics = false;
            _playTimeWasEdited = false;
            _lastPlayedWasEdited = false;
        }
    }

    private void SavePlayStatistics()
    {
        _originalItem.PlayCount = decimal.ToInt32(decimal.Clamp(
            decimal.Truncate(PlayCount),
            0,
            int.MaxValue));

        if (_playTimeWasEdited)
            _originalItem.TotalPlayTime = BuildTotalPlayTime();

        if (_lastPlayedWasEdited)
        {
            _originalItem.LastPlayed = LastPlayedDate is { } date
                ? DateTime.SpecifyKind(date.Date + (LastPlayedTime ?? TimeSpan.Zero), DateTimeKind.Local)
                : null;
        }
    }

    private TimeSpan BuildTotalPlayTime()
    {
        var hours = decimal.Clamp(decimal.Truncate(PlayTimeHours), 0, MaximumPlayTimeHours);
        var minutes = decimal.Clamp(decimal.Truncate(PlayTimeMinutes), 0, 59);
        var seconds = decimal.Clamp(decimal.Truncate(PlayTimeSeconds), 0, 59);
        var totalSeconds = hours * 3600m + minutes * 60m + seconds;
        var totalTicks = Math.Min(
            totalSeconds * TimeSpan.TicksPerSecond,
            TimeSpan.MaxValue.Ticks);

        return TimeSpan.FromTicks(decimal.ToInt64(totalTicks));
    }

    private void ClearLastPlayed()
    {
        _lastPlayedWasEdited = true;
        LastPlayedDate = null;
        LastPlayedTime = null;
    }

    private void ResetPlayStatistics()
    {
        _playTimeWasEdited = true;
        _lastPlayedWasEdited = true;
        PlayCount = 0;
        PlayTimeHours = 0;
        PlayTimeMinutes = 0;
        PlayTimeSeconds = 0;
        LastPlayedDate = null;
        LastPlayedTime = null;
    }

    partial void OnPlayTimeHoursChanged(decimal value) => MarkPlayTimeEdited();
    partial void OnPlayTimeMinutesChanged(decimal value) => MarkPlayTimeEdited();
    partial void OnPlayTimeSecondsChanged(decimal value) => MarkPlayTimeEdited();

    partial void OnLastPlayedDateChanged(DateTimeOffset? value)
    {
        if (!_isLoadingPlayStatistics)
            _lastPlayedWasEdited = true;

        ClearLastPlayedCommand.NotifyCanExecuteChanged();
    }

    partial void OnLastPlayedTimeChanged(TimeSpan? value)
    {
        if (!_isLoadingPlayStatistics)
            _lastPlayedWasEdited = true;
    }

    private void MarkPlayTimeEdited()
    {
        if (!_isLoadingPlayStatistics)
            _playTimeWasEdited = true;
    }
}
