using System;
using System.Globalization;
using Retromind.Services.RetroAchievements;

namespace Retromind.ViewModels;

/// <summary>
/// Read-only presentation state for one achievement in the desktop media details.
/// </summary>
public sealed class RetroAchievementsAchievementItemViewModel : ViewModelBase
{
    private string? _badgePath;

    public RetroAchievementsAchievementItemViewModel(RetroAchievementsAchievement achievement)
    {
        ArgumentNullException.ThrowIfNull(achievement);

        AchievementId = achievement.AchievementId;
        BadgeName = achievement.BadgeName;
        Title = achievement.Title;
        Description = achievement.Description;
        DisplayOrder = achievement.DisplayOrder;
        Points = achievement.Points;
        IsHardcore = achievement.IsEarnedHardcore;
        IsUnlocked = achievement.IsEarned || IsHardcore;
        IsCasual = IsUnlocked && !IsHardcore;
        IsLocked = !IsUnlocked;

        PointsText = string.Format(
            CultureInfo.CurrentCulture,
            T("RetroAchievements_AchievementPointsFormat", "{0:N0} points"),
            Points);

        var stateText = IsHardcore
            ? T("RetroAchievements_AchievementHardcore", "Hardcore")
            : IsCasual
                ? T("RetroAchievements_AchievementCasual", "Casual")
                : T("RetroAchievements_AchievementLocked", "Locked");
        var earnedAtUtc = IsHardcore
            ? achievement.EarnedHardcoreAtUtc
            : achievement.EarnedAtUtc;

        StatusText = earnedAtUtc.HasValue
            ? string.Format(
                CultureInfo.CurrentCulture,
                T("RetroAchievements_AchievementUnlockedAtFormat", "{0} · Unlocked {1:g}"),
                stateText,
                earnedAtUtc.Value.ToLocalTime())
            : stateText;
    }

    public int AchievementId { get; }
    public string BadgeName { get; }
    public string Title { get; }
    public string Description { get; }
    public int DisplayOrder { get; }
    public int Points { get; }
    public string PointsText { get; }
    public string StatusText { get; }
    public bool IsUnlocked { get; }
    public bool IsCasual { get; }
    public bool IsHardcore { get; }
    public bool IsLocked { get; }
    public double VisualOpacity => IsUnlocked ? 1.0 : 0.58;

    public string? BadgePath
    {
        get => _badgePath;
        private set => SetProperty(ref _badgePath, value);
    }

    internal void SetBadgePath(string? path) => BadgePath = path;
}
