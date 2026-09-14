using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class MainWindowViewModelGogAuthTests
{
    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData(null, false)]
    public void IsGogBrowserLoginForced_RecognizesSupportedValues(string? value, bool expected)
    {
        using var environment = new EnvironmentVariableScope(
            ("RETROMIND_GOG_FORCE_BROWSER_LOGIN", value));

        Assert.Equal(expected, MainWindowViewModel.IsGogBrowserLoginForced());
    }
}
