using GameClub.Agent.Configuration;
using GameClub.Agent.Security;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace GameClub.Server.Tests.Security;

public sealed class ServerEndpointPolicyTests
{
    [Fact]
    public void ProductionHttp_IsRejectedEvenWhenFlagIsTrue()
    {
        var action = () => ServerEndpointPolicy.Validate(
            new ServerOptions { BaseUrl = "http://localhost:5000" },
            new SecurityOptions { AllowInsecureDevelopmentHttp = true },
            new TestEnvironment("Production"));

        Assert.Throws<InvalidOperationException>(action);
    }

    [Fact]
    public void DevelopmentHttp_RequiresExplicitFlag()
    {
        var action = () => ServerEndpointPolicy.Validate(
            new ServerOptions { BaseUrl = "http://localhost:5000" },
            new SecurityOptions(),
            new TestEnvironment("Development"));

        Assert.Throws<InvalidOperationException>(action);
    }

    [Fact]
    public void Https_IsAcceptedInProduction()
    {
        var result = ServerEndpointPolicy.Validate(
            new ServerOptions { BaseUrl = "https://gameclub.example" },
            new SecurityOptions(),
            new TestEnvironment("Production"));

        Assert.Equal(Uri.UriSchemeHttps, result.Scheme);
    }

    private sealed class TestEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "GameClub.Agent.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
