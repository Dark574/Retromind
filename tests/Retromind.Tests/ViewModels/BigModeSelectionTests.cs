using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Reflection;
using System.Runtime.CompilerServices;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class BigModeSelectionTests
{
    [Fact]
    public void UnusedWindows_AreNotBuiltDuringRapidSelection()
    {
        var vm = CreateShell();
        for (var index = 500; index < 1000; index++)
        {
            Select(vm, index);
            Refresh(vm);
            Assert.Equal(index + 1, vm.CurrentGameNumber);
        }
        Assert.Empty(Field<RangeObservableCollection<MediaItem>>(vm, "_circularItems"));
        Assert.Empty(Field<RangeObservableCollection<MediaItem>>(vm, "_wheelItems"));
        Assert.Empty(Field<RangeObservableCollection<MediaNode>>(vm, "_wheelCategories"));
    }

    [Fact]
    public void ArcadeBinding_ActivatesOnlyCircularWindowAndRetainsRecycling()
    {
        var vm = CreateShell();
        Select(vm, 500);
        var circular = vm.CircularItems;
        var actions = new List<NotifyCollectionChangedAction>();
        circular.CollectionChanged += (_, e) => actions.Add(e.Action);
        Select(vm, 501);
        Refresh(vm);
        Assert.Same(vm.Items[501], circular[4]);
        Assert.Equal([NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Add], actions);
        Assert.Empty(Field<RangeObservableCollection<MediaItem>>(vm, "_wheelItems"));
    }

    [Fact]
    public void WheelIndexBindingBeforeItemsBinding_ActivatesMatchingWindow()
    {
        var vm = CreateShell();
        Select(vm, 500);
        Assert.Equal(4, vm.WheelSelectedItemIndex);
        Assert.Same(vm.Items[500], vm.WheelItems[4]);
        Assert.Equal(2, vm.WheelSelectedCategoryIndex);
        Assert.Same(vm.CurrentCategories[2], vm.WheelCategories[2]);
        Assert.Empty(Field<RangeObservableCollection<MediaItem>>(vm, "_circularItems"));
    }

    [Fact]
    public void ThemeSwitch_StopsUnusedUpdatesAndRehydratesExistingCollectionOnReturn()
    {
        var vm = CreateShell();
        Select(vm, 500);
        var window = vm.WheelItems;
        vm.ResetThemeWindowUsage();
        Select(vm, 900);
        Refresh(vm);
        Assert.Same(vm.Items[500], window[4]);
        Assert.Same(window, vm.WheelItems);
        Assert.Same(vm.Items[900], window[4]);
    }

    [Fact]
    public void ClearedGameList_ClearsActiveCircularWindowAndCounters()
    {
        var vm = CreateShell();
        _ = vm.CircularItems;
        vm.Items.Clear();
        Set(vm, "_selectedItem", null);
        Refresh(vm);
        Assert.Empty(vm.CircularItems);
        Assert.Equal(0, vm.TotalGames);
        Assert.Equal(0, vm.CurrentGameNumber);
    }

    private static BigModeViewModel CreateShell()
    {
        // Exercise real selection/window methods without native VLC or SDL startup.
        var vm = (BigModeViewModel)RuntimeHelpers.GetUninitializedObject(typeof(BigModeViewModel));
        Set(vm, "_items", new ObservableCollection<MediaItem>(Enumerable.Range(0, 2000).Select(i => new MediaItem($"Game {i}"))));
        var categories = new ObservableCollection<MediaNode>(Enumerable.Range(0, 4).Select(_ => new MediaNode()));
        Set(vm, "_currentCategories", categories);
        Set(vm, "_selectedCategory", categories[2]);
        Set(vm, "_selectedCategoryIndex", 2);
        Set(vm, "_circularItems", new RangeObservableCollection<MediaItem>());
        Set(vm, "_wheelItems", new RangeObservableCollection<MediaItem>());
        Set(vm, "_wheelCategories", new RangeObservableCollection<MediaNode>());
        Set(vm, "_circularWindowSize", 9);
        Set(vm, "_isGameListActive", true);
        Select(vm, 0);
        return vm;
    }

    private static void Select(BigModeViewModel vm, int index)
    {
        Set(vm, "_selectedItemIndex", index);
        Set(vm, "_selectedItem", vm.Items[index]);
    }
    private static void Refresh(BigModeViewModel vm)
    {
        foreach (var method in new[] { "UpdateGameCounters", "UpdateCircularItems", "UpdateWheelItems", "UpdateWheelCategories" })
            typeof(BigModeViewModel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);
    }
    private static T Field<T>(object vm, string name) => (T)typeof(BigModeViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
    private static void Set(object vm, string name, object? value) => typeof(BigModeViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, value);
}
