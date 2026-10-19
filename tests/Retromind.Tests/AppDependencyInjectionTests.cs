using Microsoft.Extensions.DependencyInjection;
using Retromind.Services.Stores.Gog.Auth;
using Retromind.Services.Stores.Gog.Comet;
using Retromind.Services.Stores.Security;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests;

public sealed class AppDependencyInjectionTests
{
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
