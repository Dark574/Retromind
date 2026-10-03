using Retromind.Helpers;

namespace Retromind.Tests.Helpers;

public sealed class BigModePageNavigationHelperTests
{
    [Theory]
    [InlineData(0, 25, 10, 1, 10)]
    [InlineData(10, 25, 10, 1, 20)]
    [InlineData(20, 25, 10, 1, 24)]
    [InlineData(24, 25, 10, 1, 0)]
    [InlineData(24, 25, 10, -1, 14)]
    [InlineData(14, 25, 10, -1, 4)]
    [InlineData(4, 25, 10, -1, 0)]
    [InlineData(0, 25, 10, -1, 24)]
    public void GetTargetIndex_UsesBoundariesBeforeWrapping(
        int currentIndex,
        int itemCount,
        int pageSize,
        int direction,
        int expected)
    {
        Assert.Equal(expected, BigModePageNavigationHelper.GetTargetIndex(
            currentIndex,
            itemCount,
            pageSize,
            direction));
    }

    [Theory]
    [InlineData(-1, 5, 1, 0)]
    [InlineData(-1, 5, -1, 4)]
    [InlineData(0, 0, 1, -1)]
    public void GetTargetIndex_HandlesMissingSelectionAndEmptyLists(
        int currentIndex,
        int itemCount,
        int direction,
        int expected)
    {
        Assert.Equal(expected, BigModePageNavigationHelper.GetTargetIndex(
            currentIndex,
            itemCount,
            pageSize: 10,
            direction));
    }
}
