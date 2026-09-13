using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Retromind.Models;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class MediaGridResizeTests
{
    [Fact]
    public void Category_ResizeWithinSameColumnCount_ReusesRowsAndUpdatesTileWidth()
    {
        var node = CreateNode();
        using var vm = new MediaAreaViewModel(node, 150) { ViewportWidth = 1200 };
        vm.SelectedMediaItem = node.Items[3];
        vm.MultiSelection.Toggle(node.Items[3]);

        AssertRowsUnchanged(vm.ItemRows, () =>
        {
            for (var width = 1201; width <= 1300; width++)
                vm.ViewportWidth = width;
        });

        Assert.Equal(8, vm.ColumnCount);
        Assert.Equal(162.5, vm.EffectiveItemWidth);
        Assert.Same(node.Items[3], vm.SelectedMediaItem);
        Assert.True(vm.MultiSelection.Contains(node.Items[3]));
    }

    [Fact]
    public void Search_ResizeWithinSameColumnCount_ReusesRowsAndUpdatesTileWidth()
    {
        var node = CreateNode();
        using var vm = new SearchAreaViewModel([]);
        vm.SearchResults.AddRange(node.Items);
        vm.ViewportWidth = 1200;
        vm.SelectedMediaItem = node.Items[3];
        vm.MultiSelection.Toggle(node.Items[3]);

        AssertRowsUnchanged(vm.ItemRows, () =>
        {
            for (var width = 1201; width <= 1300; width++)
                vm.ViewportWidth = width;
        });

        Assert.Equal(8, vm.ColumnCount);
        Assert.Equal(162.5, vm.EffectiveItemWidth);
        Assert.Same(node.Items[3], vm.SelectedMediaItem);
        Assert.True(vm.MultiSelection.Contains(node.Items[3]));
    }

    [Fact]
    public void Category_ColumnCountOrTileSizeChanges_RegroupsOnlyWhenNeeded()
    {
        var node = CreateNode();
        using var vm = new MediaAreaViewModel(node, 150) { ViewportWidth = 1200 };

        AssertRowsUnchanged(vm.ItemRows, () => vm.ItemWidth = 149);
        vm.ViewportWidth = 900;
        Assert.Equal(6, vm.ColumnCount);
        Assert.Equal(new[] { 6, 5 }, vm.ItemRows.Select(row => row.Items.Count));
        Assert.Equal(node.Items, vm.ItemRows.SelectMany(row => row.Items));

        vm.ItemWidth = 200;
        Assert.Equal(4, vm.ColumnCount);
        Assert.Equal(new[] { 4, 4, 3 }, vm.ItemRows.Select(row => row.Items.Count));
        Assert.Equal(node.Items, vm.ItemRows.SelectMany(row => row.Items));
        AssertRowsUnchanged(vm.ItemRows, () => vm.ViewportWidth = 901);
    }

    [Fact]
    public void Search_ColumnCountOrTileSizeChanges_RegroupsOnlyWhenNeeded()
    {
        var node = CreateNode();
        using var vm = new SearchAreaViewModel([]);
        vm.SearchResults.AddRange(node.Items);
        vm.ViewportWidth = 1200;

        AssertRowsUnchanged(vm.ItemRows, () => vm.ItemWidth = 149);
        vm.ViewportWidth = 900;
        Assert.Equal(6, vm.ColumnCount);
        Assert.Equal(new[] { 6, 5 }, vm.ItemRows.Select(row => row.Items.Count));
        Assert.Equal(node.Items, vm.ItemRows.SelectMany(row => row.Items));

        vm.ItemWidth = 200;
        Assert.Equal(4, vm.ColumnCount);
        Assert.Equal(new[] { 4, 4, 3 }, vm.ItemRows.Select(row => row.Items.Count));
        Assert.Equal(node.Items, vm.ItemRows.SelectMany(row => row.Items));
        AssertRowsUnchanged(vm.ItemRows, () => vm.ViewportWidth = 901);
    }

    [Fact]
    public void Category_FirstRealWidthAndReturnFromZeroWidth_BuildRowsEvenWithOneColumn()
    {
        var node = CreateNode();
        using var vm = new MediaAreaViewModel(node, 150);
        Assert.Empty(vm.ItemRows);

        vm.ViewportWidth = 100;
        Assert.Equal(1, vm.ColumnCount);
        Assert.Equal(node.Items.Count, vm.ItemRows.Count);

        vm.ViewportWidth = 0;
        Assert.Empty(vm.ItemRows);
        vm.ViewportWidth = 100;
        Assert.Equal(node.Items, vm.ItemRows.SelectMany(row => row.Items));
    }

    [Fact]
    public void Category_ChangedItemsWithSameCountAndColumns_StillRebuildRows()
    {
        using var vm = new MediaAreaViewModel(CreateNode(), 150) { ViewportWidth = 1200 };
        var replacements = Enumerable.Range(0, 11).Select(index => new MediaItem($"Replacement {index}")).ToArray();

        vm.RefreshItems(replacements);

        Assert.Equal(8, vm.ColumnCount);
        Assert.Equal(replacements, vm.ItemRows.SelectMany(row => row.Items));
        AssertRowsUnchanged(vm.ItemRows, () => vm.ViewportWidth = 1201);
    }

    [Fact]
    public void EmptyGrids_ResizeDoesNotResetEmptyRowCollections()
    {
        using var category = new MediaAreaViewModel(new MediaNode("Empty", NodeType.Area), 150);
        using var search = new SearchAreaViewModel([]);

        AssertRowsUnchanged(category.ItemRows, () => category.ViewportWidth = 1200);
        AssertRowsUnchanged(search.ItemRows, () => search.ViewportWidth = 1200);
        Assert.Equal(8, category.ColumnCount);
        Assert.Equal(8, search.ColumnCount);
    }

    private static MediaNode CreateNode()
        => new("Games", NodeType.Area)
        {
            Items = new ObservableCollection<MediaItem>(
                Enumerable.Range(0, 11).Select(index => new MediaItem($"Item {index}")))
        };

    private static void AssertRowsUnchanged<T>(ObservableCollection<T> rows, Action changeLayout)
        where T : class
    {
        var originalRows = rows.ToArray();
        var notifications = 0;
        NotifyCollectionChangedEventHandler handler = (_, _) => notifications++;
        rows.CollectionChanged += handler;
        try
        {
            changeLayout();
        }
        finally
        {
            rows.CollectionChanged -= handler;
        }

        Assert.Equal(0, notifications);
        Assert.Equal(originalRows.Length, rows.Count);
        for (var index = 0; index < originalRows.Length; index++)
            Assert.Same(originalRows[index], rows[index]);
    }
}
