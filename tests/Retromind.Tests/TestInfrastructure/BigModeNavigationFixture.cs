using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Retromind.Extensions;
using Retromind.Helpers.Video;
using Retromind.Models;
using Retromind.Services;
using Retromind.Services.BigMode;
using Retromind.Services.RetroAchievements;
using Retromind.ViewModels;

namespace Retromind.Tests.TestInfrastructure;

/// <summary>Runs the production VM adapter on the managed UI dispatcher without native VLC/SDL.
/// Mirrors the existing selection/diagnostics shell fixtures; playback itself is not exercised.</summary>
internal sealed class BigModeNavigationFixture : IDisposable
{
    internal BigModeViewModel Vm { get; }
    internal AppSettings Settings { get; }
    internal BigModeNavigationSession Session => Get<BigModeNavigationSession>("_navigation");

    internal BigModeNavigationFixture(ObservableCollection<MediaNode> roots,
        NavigationPresentation presentation = NavigationPresentation.MixedEntries,
        bool parentalFilter = false, bool home = false, AppSettings? settings = null)
    {
        Vm = (BigModeViewModel)RuntimeHelpers.GetUninitializedObject(typeof(BigModeViewModel));
        // Populate managed collection initializers skipped by GetUninitializedObject.
        foreach (var field in typeof(BigModeViewModel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            var type = field.FieldType;
            if (type.IsGenericType && type.GetConstructor(Type.EmptyTypes) != null &&
                (type.Namespace == "System.Collections.Generic" || type.Namespace == "System.Collections.ObjectModel" ||
                 type.Namespace == "Retromind.Helpers")) field.SetValue(Vm, Activator.CreateInstance(type));
        }
        Settings = settings ?? new AppSettings();
        Set("_rootNodes", roots);
        Set("_settings", Settings);
        Set("_theme", new Theme(new Border(), new ThemeSounds(), AppContext.BaseDirectory,
            primaryVideoEnabled: false, secondaryVideoEnabled: false, supportsHome: home,
            navigationPresentation: presentation));
        Set("_parentalFilterActive", parentalFilter);
        Set("_circularWindowSize", 9);
        Set("_selectedItemIndex", -1);
        Set("_selectedCategoryIndex", -1);
        Set("_mainVideoActiveSurfaceIndex", -1);
        Set("_activePreviewIndex", -1);
        Set("_videoDiagnosticsLock", Activator.CreateInstance(typeof(System.Threading.Lock)));
        Set("_previewMediaLock", Activator.CreateInstance(typeof(System.Threading.Lock)));
        Set("_videoPlaybackExpected", new int[3]);
        Set("_videoPlaybackGenerations", new int[3]);
        Set("_videoPlaybackFailureReported", new int[3]);
        Set("_videoSurfaceA", new LibVlcVideoSurface());
        Set("_videoSurfaceB", new LibVlcVideoSurface());
        Set("_secondaryVideoSurface", new LibVlcVideoSurface());
        Set("<RetroAchievementsProgress>k__BackingField", new RetroAchievementsProgressViewModel(
            Settings, new Progress(), new Badges()));
        Call("InitializeNavigation");
        Call("InitializeHome", false);
    }

    internal void Call(string name, params object?[] args) =>
        typeof(BigModeViewModel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Vm, args);
    internal Task CallAsync(string name, params object?[] args) =>
        (Task)typeof(BigModeViewModel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Vm, args)!;
    internal T Get<T>(string name) => (T)typeof(BigModeViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Vm)!;
    internal void Set(string name, object? value) => typeof(BigModeViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Vm, value);
    public void Dispose()
    {
        Set("_disposed", 1);
        foreach (var method in new[] { "DisposeNavigation", "StopAttractModeTimer", "DisposePreviewDebounceTimer",
                     "DisposeActiveBezelDebounceTimer", "DisposeHome", "DisposeRetroAchievementsProgress" }) Call(method);
        Get<LibVlcVideoSurface>("_videoSurfaceA").Dispose();
        Get<LibVlcVideoSurface>("_videoSurfaceB").Dispose();
        Get<LibVlcVideoSurface>("_secondaryVideoSurface").Dispose();
    }
    private sealed class Progress : IRetroAchievementsProgressService
    {
        public Task<RetroAchievementsProgressSnapshot> GetProgressAsync(int gameId, bool forceRefresh = false,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("No network in navigation tests");
    }
    private sealed class Badges : IRetroAchievementsBadgeService
    {
        public Task<string?> GetBadgePathAsync(string? badgeName, bool isUnlocked,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }
}
