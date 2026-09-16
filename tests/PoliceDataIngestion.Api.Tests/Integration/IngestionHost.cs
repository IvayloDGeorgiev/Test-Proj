using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PoliceDataIngestion.Api.Tests.PoliceApi;
using Test_Proj.Clients.PoliceApi;
using Test_Proj.Export;

namespace PoliceDataIngestion.Api.Tests.Integration;

internal sealed class IngestionHost : WebApplicationFactory<Program>
{
    public string Sandbox { get; } = Path.Combine(Path.GetTempPath(), "PoliceHostTests", Guid.NewGuid().ToString("N"));
    public string OutputRoot => Path.Combine(Sandbox, "exports");
    public string ContentRoot => Path.Combine(Sandbox, "host");
    public ManualClock Clock { get; } = new();
    public LogSink Logs { get; } = new();
    public ScriptedHandler Handler { get; }
    public FaultFiles Files { get; } = new();
    public Dictionary<string, string?> Settings { get; } = new()
    {
        ["PoliceApi:BaseUrl"] = "https://data.police.uk/api/",
        ["PoliceApi:MaximumAttempts"] = "1",
        ["PoliceApi:AttemptTimeoutSeconds"] = "30",
        ["PoliceApi:TotalOperationTimeoutSeconds"] = "120",
        ["PoliceApi:MaximumResponseBytes"] = "33554432",
        ["PoliceApi:MaximumRecords"] = "100000",
        ["Export:MaximumRequestBodyBytes"] = "4096",
        ["Export:MaximumConcurrentOperations"] = "1",
        ["Export:MaximumQueuedOperations"] = "0",
        ["AllowedHosts"] = "localhost",
        ["Logging:LogLevel:Default"] = "Information",
        ["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning",
        ["Database:ApplyMigrations"] = "false"
    };
    public string EnvironmentName { get; set; } = "Production";
    public string? WebRoot { get; set; }

    public IngestionHost(Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null)
    {
        Directory.CreateDirectory(ContentRoot);
        Settings["Export:OutputRoot"] = OutputRoot;
        Handler = new(send ?? ((_, _, _) => Task.FromResult(Response("[]"))));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Set before the entry point loads Development-only optional configuration.
        builder.UseContentRoot(ContentRoot).UseEnvironment(EnvironmentName);
        if (WebRoot is not null) builder.UseWebRoot(WebRoot);
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.Sources.Clear();
            configuration.AddInMemoryCollection(Settings);
        });
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddProvider(Logs);
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.RemoveAll<IAtomicFileOperations>();
            services.AddSingleton<IAtomicFileOperations>(Files);
            services.AddHttpClient<IPoliceApiClient, PoliceApiClient>()
                .ConfigurePrimaryHttpMessageHandler(() => Handler).RemoveAllLoggers();
        });
    }

    public HttpClient Client() => CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
    public static HttpResponseMessage Response(string body, int status = 200) =>
        new((HttpStatusCode)status) { Content = new StringContent(body) };

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        // Only remove this fixture's resolved, uniquely created sandbox.
        var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PoliceHostTests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(Sandbox).StartsWith(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid test sandbox.");
        Directory.Delete(Sandbox, recursive: true);
    }
}

internal sealed class FaultFiles : IAtomicFileOperations
{
    private readonly AtomicFileOperations real = new();
    public Func<string, Stream>? Create { get; set; }
    public Action? BeforePublish { get; set; }
    public Action? AfterPublish { get; set; }
    public bool FailCleanup { get; set; }
    public int Publications { get; private set; }
    public int Creates { get; private set; }
    public Stream CreateTemporary(string path) { Creates++; return Create?.Invoke(path) ?? real.CreateTemporary(path); }
    public void Publish(string temporary, string destination)
    {
        BeforePublish?.Invoke();
        real.Publish(temporary, destination);
        Publications++;
        AfterPublish?.Invoke();
    }
    public void DeleteTemporary(string path)
    {
        if (FailCleanup) throw new IOException("synthetic-private-marker");
        real.DeleteTemporary(path);
    }
}

internal sealed class LogSink : ILoggerProvider
{
    public ConcurrentQueue<(string Category, string Message, Exception? Exception)> Entries { get; } = new();
    public TaskCompletionSource CallerCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ILogger CreateLogger(string categoryName) => new SinkLogger(this, categoryName);
    public void Dispose() { }
    private sealed class SinkLogger(LogSink sink, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            sink.Entries.Enqueue((category, message, exception));
            if (category == "Test_Proj.Errors.IngestionErrorMiddleware" && message.Contains("caller_cancelled"))
                sink.CallerCancelled.TrySetResult();
        }
    }
}
