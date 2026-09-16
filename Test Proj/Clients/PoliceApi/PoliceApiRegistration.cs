using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Test_Proj.Clients.PoliceApi;

public static class PoliceApiRegistration
{
    public static IServiceCollection AddPoliceApiClient(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IRetryJitter, RetryJitter>();
        services.AddHttpClient<IPoliceApiClient, PoliceApiClient>(client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.MaxResponseContentBufferSize = 33_554_432;
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            MaxResponseHeadersLength = 32
        }).RemoveAllLoggers(); // Factory defaults include full query URLs; emit safe structured events ourselves.
        return services;
    }
}
