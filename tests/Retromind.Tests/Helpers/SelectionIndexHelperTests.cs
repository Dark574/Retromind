using System.Collections;
using Retromind.Extensions;
using Retromind.Helpers;

namespace Retromind.Tests.Helpers;

public sealed class SelectionIndexHelperTests
{
    [Fact]
    public void LargeLibrary_IndexedAndLegacyPathsMatchWithMeasuredSearchCounts()
    {
        var legacy = new CountingList(50_000);
        var indexed = new CountingList(50_000);
        var oldWindow = new RangeObservableCollection<object>();
        var newWindow = new RangeObservableCollection<object>();
        for (var index = 25_000; index < 26_000; index++)
        {
            CircularWindowHelper.SynchronizeCircularWindow(legacy, legacy[index], 9, oldWindow);
            CircularWindowHelper.SynchronizeCircularWindow(indexed, indexed[index], 9, newWindow, index);
            // The independent libraries use different objects; compare source positions.
            Assert.Same(legacy[index - 4], oldWindow[0]);
            Assert.Same(indexed[index - 4], newWindow[0]);
            Assert.Same(legacy[index + 4], oldWindow[8]);
            Assert.Same(indexed[index + 4], newWindow[8]);
        }
        Assert.Equal(1000, legacy.Searches);
        Assert.Equal(25_500_500, legacy.ElementsSearched);
        Assert.Equal(0, indexed.Searches);
        Assert.Equal(0, indexed.ElementsSearched);
    }

    [Fact]
    public void LargeLibrary_ControllerStepsAndBothWindowsAvoidLinearSearches()
    {
        var source = new CountingList(50_000);
        var circular = new RangeObservableCollection<object>();
        var wheel = new RangeObservableCollection<object>();
        var lookahead = new List<object>();
        for (var index = 25_000; index < 26_000; index++)
        {
            var selected = source[index];
            Assert.Equal(index, SelectionIndexHelper.Resolve(source, selected, index));
            CircularWindowHelper.SynchronizeCircularWindow(source, selected, 11, circular, index);
            Assert.Equal(4, CircularWindowHelper.SynchronizeRepeatingCircularWindow(
                source, selected, 9, 5, wheel, index));
            CircularWindowHelper.BuildCircularLookahead(source, selected, 11, 2, lookahead, index);
            Assert.Same(selected, circular[5]);
            Assert.Same(selected, wheel[4]);
        }
        Assert.Equal(0, source.Searches);
        Assert.Equal(0, source.ElementsSearched);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(50000)]
    [InlineData(1)]
    public void InvalidIndex_ExternalSelectionFallsBackExactlyOnce(int hint)
    {
        var source = new CountingList(50_000);
        Assert.Equal(42_000, SelectionIndexHelper.Resolve(source, source[42_000], hint));
        Assert.Equal(1, source.Searches);
        Assert.Equal(42_001, source.ElementsSearched);
    }

    [Fact]
    public void ReplacementCollectionAndMissingSelectionCannotReuseStaleIndex()
    {
        var source = new CountingList(10);
        var selected = source[4];
        var replacement = new CountingList(10);
        Assert.Equal(-1, SelectionIndexHelper.Resolve(replacement, selected, 4));
        Assert.Equal(-1, SelectionIndexHelper.Resolve(source, null, 4));
        Assert.Equal(0, source.Searches);
        source.RemoveAt(0);
        Assert.Equal(3, SelectionIndexHelper.Resolve(source, selected, 4));
    }

    [Fact]
    public void ExistingPublicWindowOverloadPreservesSearchAndWraparound()
    {
        var source = new CountingList(50_000);
        var target = new RangeObservableCollection<object>();
        CircularWindowHelper.SynchronizeCircularWindow(source, source[49_999], 9, target);
        Assert.Equal(1, source.Searches);
        Assert.Equal(50_000, source.ElementsSearched);
        Assert.Same(source[0], target[5]);
    }

    private sealed class CountingList(int count) : IList<object>
    {
        private readonly List<object> _items = Enumerable.Range(0, count).Select(_ => new object()).ToList();
        public int Searches { get; private set; }
        public int ElementsSearched { get; private set; }
        public int IndexOf(object item)
        {
            Searches++;
            for (var i = 0; i < Count; i++)
            {
                ElementsSearched++;
                if (ReferenceEquals(_items[i], item)) return i;
            }
            return -1;
        }
        public object this[int index] { get => _items[index]; set => _items[index] = value; }
        public int Count => _items.Count;
        public bool IsReadOnly => false;
        public void Add(object item) => _items.Add(item);
        public void Clear() => _items.Clear();
        public bool Contains(object item) => _items.Contains(item);
        public void CopyTo(object[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
        public IEnumerator<object> GetEnumerator() => _items.GetEnumerator();
        public void Insert(int index, object item) => _items.Insert(index, item);
        public bool Remove(object item) => _items.Remove(item);
        public void RemoveAt(int index) => _items.RemoveAt(index);
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
