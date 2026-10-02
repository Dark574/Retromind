using Retromind.Helpers;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Helpers;

public sealed class ApplicationInstanceLockTests
{
    [Fact]
    public void Acquire_RejectsSecondInstanceForSameDataRoot()
    {
        using var temp = new TemporaryDirectory();
        using var first = ApplicationInstanceLock.Acquire(temp.RootPath);

        Assert.Throws<IOException>(() => ApplicationInstanceLock.Acquire(temp.RootPath));
    }

    [Fact]
    public void Acquire_AllowsNewInstanceAfterPreviousInstanceExits()
    {
        using var temp = new TemporaryDirectory();

        using (ApplicationInstanceLock.Acquire(temp.RootPath))
        {
        }

        using var next = ApplicationInstanceLock.Acquire(temp.RootPath);
    }

    [Fact]
    public void Acquire_AllowsSeparatePortableDataRoots()
    {
        using var firstRoot = new TemporaryDirectory();
        using var secondRoot = new TemporaryDirectory();
        using var first = ApplicationInstanceLock.Acquire(firstRoot.RootPath);

        using var second = ApplicationInstanceLock.Acquire(secondRoot.RootPath);
    }

    [Fact]
    public void Acquire_DoesNotTreatExistingUnlockedFileAsRunningInstance()
    {
        using var temp = new TemporaryDirectory();
        File.WriteAllText(temp.GetPath(".retromind.instance.lock"), string.Empty);

        using var instanceLock = ApplicationInstanceLock.Acquire(temp.RootPath);
    }
}
