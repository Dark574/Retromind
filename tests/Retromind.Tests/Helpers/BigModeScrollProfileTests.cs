using Retromind.Helpers;

namespace Retromind.Tests.Helpers;

public sealed class BigModeScrollProfileTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(350, 1)]
    [InlineData(999, 1)]
    [InlineData(1000, 2)]
    [InlineData(1999, 2)]
    [InlineData(2000, 5)]
    [InlineData(2999, 5)]
    [InlineData(3000, 10)]
    [InlineData(4999, 10)]
    [InlineData(5000, 20)]
    [InlineData(7999, 20)]
    [InlineData(8000, 40)]
    [InlineData(60000, 40)]
    public void DirectionProfile_HasExactMonotonicThresholds(int milliseconds, int step) =>
        Assert.Equal(step, BigModeScrollProfile.GetStep(TimeSpan.FromMilliseconds(milliseconds), 10000, page: false));

    [Theory]
    [InlineData(0, 10)]
    [InlineData(350, 10)]
    [InlineData(1999, 10)]
    [InlineData(2000, 20)]
    [InlineData(2999, 20)]
    [InlineData(3000, 30)]
    [InlineData(4999, 30)]
    [InlineData(5000, 40)]
    [InlineData(6999, 40)]
    [InlineData(7000, 50)]
    [InlineData(60000, 50)]
    public void PageProfile_IsSeparateAndBounded(int milliseconds, int step) =>
        Assert.Equal(step, BigModeScrollProfile.GetStep(TimeSpan.FromMilliseconds(milliseconds), 10000, page: true));

    [Theory]
    [InlineData(0, 1, 10)]
    [InlineData(1, 1, 10)]
    [InlineData(9, 1, 10)]
    [InlineData(19, 1, 10)]
    [InlineData(20, 2, 10)]
    [InlineData(50, 5, 10)]
    [InlineData(100, 10, 10)]
    [InlineData(250, 25, 25)]
    [InlineData(10000, 40, 50)]
    public void SmallLists_LimitAccelerationWithoutChangingPageTaps(int count, int directionStep, int pageStep)
    {
        Assert.Equal(directionStep, BigModeScrollProfile.GetStep(TimeSpan.FromSeconds(30), count, page: false));
        Assert.Equal(pageStep, BigModeScrollProfile.GetStep(TimeSpan.FromSeconds(30), count, page: true));
    }
}
