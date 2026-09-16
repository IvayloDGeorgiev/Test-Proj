using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Test_Proj.Clients.PoliceApi;
using Test_Proj.Options;

namespace PoliceDataIngestion.Api.Tests.PoliceApi;

public sealed class RegistrationTests
{
    [Fact]
    public void Registration_ProductionHandler_DisablesRedirectsCookiesAndUrlLogging()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddPoliceApiClient();
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>().Get(nameof(IPoliceApiClient));
        var builder = new InspectionBuilder(provider);
        // Act
        foreach (var action in options.HttpMessageHandlerBuilderActions) action(builder);
        // Assert
        using var handler = Assert.IsType<SocketsHttpHandler>(builder.PrimaryHandler);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Equal(System.Net.DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.Equal(32, handler.MaxResponseHeadersLength);
        // Inspect the factory's actual built pipeline without sending any network traffic.
        var pipeline = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(nameof(IPoliceApiClient));
        while (pipeline is DelegatingHandler delegating)
        {
            Assert.DoesNotContain("Logging", delegating.GetType().Name);
            pipeline = delegating.InnerHandler!;
        }
    }

    [Fact]
    public async Task Registration_TypedClient_UsesFakeHandlerAndInfiniteHttpClientTimeout()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddPoliceApiClient();
        var handler = new ScriptedHandler((_, _, _) => Task.FromResult(TransportFixture.Response()));
        services.AddHttpClient<IPoliceApiClient, PoliceApiClient>().ConfigurePrimaryHttpMessageHandler(() => handler);
        using var provider = services.BuildServiceProvider();
        // Act
        var result = await provider.GetRequiredService<IPoliceApiClient>().GetForcesAsync();
        using var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IPoliceApiClient));
        // Assert
        Assert.Empty(result);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);
    }

    [Theory]
    [InlineData("http://data.police.uk/api/")] [InlineData("https://other.invalid/api/")]
    [InlineData("https://data.police.uk/api/?secret=x")]
    public void Constructor_UnsafeOrigin_RejectsWithoutSending(string origin)
    {
        // Arrange
        using var handler = new ScriptedHandler((_, _, _) => throw new InvalidOperationException("Must not send"));
        using var http = new HttpClient(handler);
        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => new PoliceApiClient(http,
            Microsoft.Extensions.Options.Options.Create(new PoliceApiOptions { BaseUrl = origin }),
            TimeProvider.System, new RetryJitter(), NullLogger<PoliceApiClient>.Instance));
        // Assert
        Assert.Equal("Police API configuration is invalid.", exception.Message);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Constructor_OptionsMutatedAfterConstruction_PreservesTrustedSnapshot()
    {
        // Arrange
        var options = new PoliceApiOptions();
        using var fixture = new TransportFixture((_, _, _) => Task.FromResult(TransportFixture.Response()), options);
        options.BaseUrl = "https://untrusted.invalid/";
        options.MaximumResponseBytes = 1;
        // Act
        var result = await fixture.Client.GetForcesAsync();
        // Assert
        Assert.Empty(result);
        Assert.Equal("data.police.uk", Assert.Single(fixture.Handler.Uris).Host);
    }

    private sealed class InspectionBuilder(IServiceProvider services) : HttpMessageHandlerBuilder
    {
        public override string? Name { get; set; }
        public override HttpMessageHandler PrimaryHandler { get; set; } = null!;
        public override IList<DelegatingHandler> AdditionalHandlers { get; } = new List<DelegatingHandler>();
        public override IServiceProvider Services => services;
        public override HttpMessageHandler Build() => CreateHandlerPipeline(PrimaryHandler, AdditionalHandlers);
    }
}
