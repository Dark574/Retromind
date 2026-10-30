using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using Retromind.Models;
using Retromind.Services;
using Retromind.Services.Stores.Gog.Auth;
using Retromind.Services.Stores.Gog.Comet;
using Retromind.Services.Stores.Security;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests;

public sealed class AppDependencyInjectionTests
{
    [Fact]
    public void ConfigureServices_ResolvesSingletonErrorServiceUsingPortableDataRoot()
    {
        using var temp = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")));
        var services = new ServiceCollection();
        typeof(App).GetMethod("ConfigureServices", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(new App(), [services]);
        using var provider = services.BuildServiceProvider();

        var errors = provider.GetRequiredService<IErrorService>();
        Assert.IsType<ErrorService>(errors);
        Assert.Same(errors, provider.GetRequiredService<IErrorService>());
        Assert.IsType<AudioService>(provider.GetRequiredService<AudioService>());
        Assert.IsType<WinetricksService>(provider.GetRequiredService<WinetricksService>());
        errors.Report(new ErrorReport("Test", ErrorSeverity.Diagnostic, "Test", "Portable log"));
        Assert.True(File.Exists(temp.GetPath("Logs", "Exceptions", "errors.jsonl")));
        Assert.False(Directory.Exists(temp.GetPath("Logs", "Launch")));
    }

    [Fact]
    public void RegisterGogCometServices_ResolvesSessionService()
    {
        using var dataRoot = new TemporaryDirectory();
        using var httpClient = new HttpClient();
        var services = new ServiceCollection();
        services.AddSingleton(new GogAuthService(
            new InMemorySecretStore(),
            new GogOAuthClient(httpClient),
            new GogPkceService()));

        App.RegisterGogCometServices(services, dataRoot.RootPath);

        using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<IGogCometSessionService>();

        Assert.IsType<GogCometSessionService>(service);
        Assert.Same(service, provider.GetRequiredService<IGogCometSessionService>());
    }
}
