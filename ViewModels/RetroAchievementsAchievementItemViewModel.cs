using System;
using System.Globalization;
using Retromind.Services.RetroAchievements;
using Retromind.Services.SteamAchievements;

namespace Retromind.ViewModels;

/// <summary>
/// Provider-neutral, read-only presentation state for one achievement.
/// The historical type name is retained for compatibility with existing themes.
/// </summary>
public sealed class RetroAchievementsAchievementItemViewModel : ViewModelBase
{
    private string? _badgePath;

    public RetroAchievementsAchievementItemViewModel(RetroAchievementsAchievement achievement)
    {
        ArgumentNullException.ThrowIfNull(achievement);

        AchievementId = achievement.AchievementId;
        AchievementKey = achievement.AchievementId.ToString(CultureInfo.InvariantCulture);
        BadgeName = achievement.BadgeName;
        Title = achievement.Title;
        Description = achievement.Description;
        DisplayOrder = achievement.DisplayOrder;
        Points = achievement.Points;
        IsHardcore = achievement.IsEarnedHardcore;
        IsUnlocked = achievement.IsEarned || IsHardcore;
        IsCasual = IsUnlocked && !IsHardcore;
        IsLocked = !IsUnlocked;
        ImageReference = achievement.BadgeName;

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

    public RetroAchievementsAchievementItemViewModel(SteamAchievement achievement)
    {
        ArgumentNullException.ThrowIfNull(achievement);

        AchievementKey = achievement.ApiName;
        Title = string.IsNullOrWhiteSpace(achievement.Title)
            ? T("SteamAchievements_HiddenAchievement", "Hidden achievement")
            : achievement.Title;
        Description = achievement.Description;
        IsHidden = achievement.IsHidden;
        IsUnlocked = achievement.IsUnlocked;
        IsLocked = !IsUnlocked;
        ImageReference = achievement.ImageUrl;
        PointsText = IsHidden
            ? T("SteamAchievements_Hidden", "Hidden")
            : string.Empty;

        var stateText = IsUnlocked
            ? T("SteamAchievements_AchievementUnlocked", "Unlocked")
            : T("RetroAchievements_AchievementLocked", "Locked");
        StatusText = achievement.UnlockedAtUtc.HasValue
            ? string.Format(
                CultureInfo.CurrentCulture,
                T("RetroAchievements_AchievementUnlockedAtFormat", "{0} · Unlocked {1:g}"),
                stateText,
                achievement.UnlockedAtUtc.Value.ToLocalTime())
            : stateText;
    }

    public int AchievementId { get; }
    public string AchievementKey { get; } = string.Empty;
    public string BadgeName { get; } = string.Empty;
    public string Title { get; } = string.Empty;
    public string Description { get; } = string.Empty;
    public int DisplayOrder { get; }
    public int Points { get; }
    public string PointsText { get; } = string.Empty;
    public string StatusText { get; } = string.Empty;
    public bool IsUnlocked { get; }
    public bool IsCasual { get; }
    public bool IsHardcore { get; }
    public bool IsLocked { get; }
    public bool IsHidden { get; }
    public double VisualOpacity => IsUnlocked ? 1.0 : 0.58;
    internal string? ImageReference { get; }

    public string? BadgePath
    {
        get => _badgePath;
        private set => SetProperty(ref _badgePath, value);
    }

    internal void SetBadgePath(string? path) => BadgePath = path;
}
