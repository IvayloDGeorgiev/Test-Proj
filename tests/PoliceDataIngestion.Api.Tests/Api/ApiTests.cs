using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PoliceDataIngestion.Api.Tests.Export;
using PoliceDataIngestion.Api.Tests.Forces;
using PoliceDataIngestion.Api.Tests.PoliceApi;
using Test_Proj.Clients.PoliceApi;
using Test_Proj.Contracts.Requests;
using Test_Proj.Contracts.Responses;
using Test_Proj.Controllers;
using Test_Proj.Errors;
using Test_Proj.Export;
using Test_Proj.Options;
using Test_Proj.Services.Forces;
using Test_Proj.Services.Crimes;
using Test_Proj.Services.StopSearches;
using Test_Proj.Validation;

namespace PoliceDataIngestion.Api.Tests.Api;

public sealed class ApiTests
{
    [Theory]
    [InlineData(4096, false, 200)]
    [InlineData(4097, false, 413)]
    [InlineData(9000, false, 413)]
    [InlineData(4096, true, 200)]
    [InlineData(4097, true, 413)]
    public async Task Input_BoundedBeforeEndpoint_EnforcesKnownAndUnknownLengths(int size, bool known, int status)
    {
        // Arrange
        using var fixture = new Pipeline();
        var original = new MemoryStream(new byte[size]);
        fixture.Context.Request.Body = original;
        fixture.Context.Request.ContentLength = known ? size : null;
        var feature = new SizeFeature();
        fixture.Context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);
        var calls = 0;
        // Act
        await fixture.Run(async c => { calls++; Assert.Equal(size, c.Request.Body.Length); await Task.CompletedTask; });
        // Assert
        Assert.Equal(status, fixture.Context.Response.StatusCode);
        Assert.Equal(status == 200 ? 1 : 0, calls);
        Assert.Equal(4096, feature.MaxRequestBodySize);
        Assert.Same(original, fixture.Context.Request.Body);
        Assert.True(original.Position <= 4097);
        if (status == 413) Assert.Contains("request_body_too_large", fixture.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deadline_PendingExport_DistinguishesCallerAndPreservesOldFile(bool callerCancel)
    {
        // Arrange
        using var caller = new CancellationTokenSource();
        using var pipeline = new Pipeline(caller.Token);
        var operations = new ExportTests.FaultOperations { Failure = "cancel-write" };
        using var files = new ExportTests.Fixture(operations);
        await File.WriteAllTextAsync(files.Final, "old complete");
        var service = new ForcesIngestionService(new ForcesTests.FakeClient(), files.Writer, files.Gate, pipeline.Clock);
        var returned = false;
        // Act
        var task = pipeline.Run(async c => { await service.IngestAsync(c.RequestAborted); returned = true; });
        await operations.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (callerCancel) caller.Cancel(); else pipeline.Clock.Advance(TimeSpan.FromSeconds(120));
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        // Assert
        Assert.False(returned);
        Assert.Equal("old complete", await File.ReadAllTextAsync(files.Final));
        Assert.Equal([files.Final], Directory.GetFiles(files.Root));
        Assert.Equal(caller.Token, pipeline.Context.RequestAborted);
        Assert.Null(OperationDeadline.Current);
        if (callerCancel) Assert.Empty(pipeline.Text);
        else { Assert.Equal(504, pipeline.Context.Response.StatusCode); Assert.Contains("operation_timeout", pipeline.Text); }
        using var available = files.Gate.Acquire();
    }

    [Fact]
    public async Task Deadline_InputRead_CancelsBeforeEndpoint()
    {
        // Arrange
        using var pipeline = new Pipeline();
        using var input = new BlockingStream();
        pipeline.Context.Request.Body = input;
        var calls = 0;
        // Act
        var task = pipeline.Run(_ => { calls++; return Task.CompletedTask; });
        await input.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        pipeline.Clock.Advance(TimeSpan.FromSeconds(120));
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        // Assert
        Assert.Equal(0, calls);
        Assert.Equal(504, pipeline.Context.Response.StatusCode);
        Assert.Contains("operation_timeout", pipeline.Text);
    }

    [Fact]
    public async Task Deadline_PublicationCommitPoint_ResponseUsesCallerTokenAndLogsMetadata()
    {
        // Arrange
        using var pipeline = new Pipeline();
        var operations = new ExportTests.FaultOperations { BeforePublish = () => pipeline.Clock.Advance(TimeSpan.FromSeconds(120)) };
        using var files = new ExportTests.Fixture(operations);
        var service = new ForcesIngestionService(new ForcesTests.FakeClient(), files.Writer, files.Gate, pipeline.Clock);
        var logger = new CaptureLog<IngestionResultFilter>();
        var filter = new IngestionResultFilter(pipeline.Clock, logger);
        IngestionResult? result = null;
        // Act
        await pipeline.Run(async c =>
        {
            var action = Action(c);
            await filter.OnActionExecutionAsync(new(action, [], new Dictionary<string, object?>(), new object()), async () =>
            {
                result = await service.IngestAsync(c.RequestAborted);
                return new(action, [], new object()) { Result = new OkObjectResult(result) };
            });
            Assert.False(c.RequestAborted.IsCancellationRequested);
            await c.Response.WriteAsync("published", c.RequestAborted);
        });
        // Assert
        Assert.Equal("published", pipeline.Text);
        Assert.True(result!.Success);
        Assert.Equal("id,name\r\none,One\r\n", await File.ReadAllTextAsync(files.Final));
        Assert.Contains("forces", Assert.Single(logger.Messages));
        Assert.Contains("count 1", logger.Messages[0]);
        Assert.Contains("published", logger.Messages[0]);
        Assert.DoesNotContain(files.Root, logger.Messages[0]);
    }

    [Fact]
    public async Task RetryDelay_InputTimeConsumed_UsesRemainingOperationBudget()
    {
        // Arrange
        using var pipeline = new Pipeline();
        var response = TransportFixture.Response(429);
        response.Headers.RetryAfter = new(TimeSpan.FromSeconds(30));
        var handler = new ScriptedHandler((_, _, _) => Task.FromResult(response));
        using var http = new HttpClient(handler);
        var client = new PoliceApiClient(http, Options.Create(new PoliceApiOptions()), pipeline.Clock,
            new Jitter(), NullLogger<PoliceApiClient>.Instance);
        // Act
        await pipeline.Run(async c =>
        {
            pipeline.Clock.Advance(TimeSpan.FromSeconds(100));
            await client.GetForcesAsync(c.RequestAborted);
        });
        // Assert
        Assert.Equal(1, handler.Calls);
        Assert.Equal(503, pipeline.Context.Response.StatusCode);
        Assert.Contains("upstream_retry_budget", pipeline.Text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Admission_EachDatasetHoldsGlobalLease_RejectsAllRoutes(int active)
    {
        // Arrange
        var gate = new OperationLease();
        var client = new AllClient();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exporter = new ForcesTests.FakeExporter { OnExport = async (_, _, _, token) =>
        { entered.TrySetResult(); await resume.Task.WaitAsync(token); return 0; } };
        var clock = new ManualClock();
        var request = new LocationMonthRequest(0, 0, "2024-01");
        Func<Task<IngestionResult>>[] routes = [
            () => new ForcesIngestionService(client, exporter, gate, clock).IngestAsync(default),
            () => new CrimesIngestionService(client, exporter, gate, clock).IngestAsync(request, default),
            () => new StopSearchesIngestionService(client, exporter, gate, clock).IngestAsync(request, default)];
        var first = routes[active]();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            // Act / Assert
            foreach (var route in routes)
            {
                var busy = await Assert.ThrowsAsync<ExportException>(route);
                Assert.Equal("operation_busy", busy.Code);
                Assert.Equal(409, busy.StatusCode);
            }
            Assert.Equal(1, client.Calls);
            Assert.Equal(1, exporter.Calls);
        }
        finally { resume.TrySetResult(); }
        Assert.True((await first).Success);
        using var released = gate.Acquire();
    }

    public static TheoryData<Exception, int, string> Errors => new()
    {
        { new RequestValidationException(new Dictionary<string, string[]> { ["month"] = ["secret-token C:\\private"], ["secret-token"] = ["synthetic-upstream-body"] }), 400, "validation_failed" },
        { new RequestBodyLimitException(), 413, "request_body_too_large" },
        { new BadHttpRequestException("secret-token", 413), 413, "request_body_too_large" },
        { new BadHttpRequestException("secret-token", 400), 400, "validation_failed" },
        { new OperationTimeoutException(), 504, "operation_timeout" },
        { new PoliceApiException(PoliceApiFailure.InvalidPayload), 502, "upstream_invalid_payload" },
        { new PoliceApiException(PoliceApiFailure.ResponseLimit), 502, "upstream_limit_exceeded" },
        { new PoliceApiException(PoliceApiFailure.UpstreamFailure), 502, "upstream_failure" },
        { new PoliceApiException(PoliceApiFailure.RateLimited), 503, "upstream_rate_limited" },
        { new PoliceApiException(PoliceApiFailure.RetryBudget), 503, "upstream_retry_budget" },
        { new PoliceApiException(PoliceApiFailure.Timeout), 504, "upstream_timeout" },
        { new ExportException("operation_busy", 409), 409, "operation_busy" },
        { new ExportException("secret-token", 200), 500, "export_failed" },
        { new IOException("secret-token C:\\private synthetic-upstream-body"), 500, "internal_error" },
        { new OperationCanceledException("secret-token"), 500, "internal_error" }
    };

    [Theory]
    [MemberData(nameof(Errors))]
    public async Task Errors_StatusTable_SanitizesResponseAndLog(Exception failure, int status, string code)
    {
        // Arrange
        using var pipeline = new Pipeline();
        // Act
        await pipeline.Run(_ => Task.FromException(failure));
        // Assert
        Assert.Equal(status, pipeline.Context.Response.StatusCode);
        Assert.StartsWith("application/problem+json", pipeline.Context.Response.ContentType);
        using var json = JsonDocument.Parse(pipeline.Text);
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        Assert.Equal("api-trace", json.RootElement.GetProperty("traceId").GetString());
        Assert.False(json.RootElement.TryGetProperty("success", out _));
        if (status == 400) Assert.Equal(JsonValueKind.Object, json.RootElement.GetProperty("errors").ValueKind);
        var output = pipeline.Text + string.Join("", pipeline.Logger.Messages);
        foreach (var sensitive in new[] { "secret-token", "private", "synthetic-upstream-body", "StackTrace" }) Assert.DoesNotContain(sensitive, output);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(415)]
    public void FrameworkErrors_UnsafeDetails_ReplacedBySafeProblem(int status)
    {
        // Arrange
        var result = new ObjectResult(new ProblemDetails { Detail = "secret-token", Instance = "C:\\private" }) { StatusCode = status };
        var context = new ResultExecutingContext(Action(new DefaultHttpContext()), [], result, new object());
        var filter = new IngestionResultFilter(new ManualClock(), NullLogger<IngestionResultFilter>.Instance);
        // Act
        filter.OnResultExecuting(context);
        // Assert
        var problem = Assert.IsAssignableFrom<ProblemDetails>(result.Value);
        Assert.Null(problem.Detail);
        Assert.Null(problem.Instance);
        Assert.Equal(status == 415 ? "unsupported_media_type" : "validation_failed", problem.Extensions["code"]);
        Assert.Equal("application/problem+json", Assert.Single(result.ContentTypes));
    }

    [Theory]
    [InlineData(typeof(IngestionController), "Forces")]
    [InlineData(typeof(CrimesIngestionController), "Crimes")]
    [InlineData(typeof(StopSearchesIngestionController), "StopSearches")]
    public void OpenApi_AllRoutes_DeclareOperationalStatuses(Type controller, string method)
    {
        // Arrange / Act
        var metadata = controller.GetMethod(method)!.GetCustomAttributes<ProducesResponseTypeAttribute>().ToArray();
        // Assert
        Assert.Equal(new[] { 200, 400, 409, 413, 415, 500, 502, 503, 504 }, metadata.Select(m => m.StatusCode).Order());
        Assert.Equal(typeof(IngestionResult), metadata.Single(m => m.StatusCode == 200).Type);
        Assert.All(metadata.Where(m => m.StatusCode >= 400), m => Assert.True(typeof(ProblemDetails).IsAssignableFrom(m.Type)));
        foreach (var error in metadata.Where(m => m.StatusCode >= 400))
        {
            var contentTypes = new Microsoft.AspNetCore.Mvc.Formatters.MediaTypeCollection();
            ((Microsoft.AspNetCore.Mvc.ApiExplorer.IApiResponseMetadataProvider)error).SetContentTypes(contentTypes);
            Assert.Equal("application/problem+json", Assert.Single(contentTypes));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Upstream_ByteOrRowLimit_PreservesOldFileWithoutExportRetry(bool bytes)
    {
        // Arrange
        using var pipeline = new Pipeline();
        using var files = new ExportTests.Fixture();
        await File.WriteAllTextAsync(files.Final, "old complete");
        using var transport = new TransportFixture((_, _, _) => Task.FromResult(TransportFixture.Response(200,
            "[{\"id\":\"one\",\"name\":\"synthetic-sensitive-body\"},{\"id\":\"two\",\"name\":\"Two\"}]")),
            new PoliceApiOptions { MaximumResponseBytes = bytes ? 10 : 1024, MaximumRecords = bytes ? 100 : 1 });
        var service = new ForcesIngestionService(transport.Client, files.Writer, files.Gate, pipeline.Clock);
        // Act
        await pipeline.Run(async c => { await service.IngestAsync(c.RequestAborted); });
        // Assert
        Assert.Equal(502, pipeline.Context.Response.StatusCode);
        Assert.Contains("upstream_limit_exceeded", pipeline.Text);
        Assert.DoesNotContain("synthetic-sensitive-body", pipeline.Text + string.Join("", pipeline.Logger.Messages));
        Assert.Equal(1, transport.Handler.Calls);
        Assert.Equal("old complete", await File.ReadAllTextAsync(files.Final));
        Assert.Equal([files.Final], Directory.GetFiles(files.Root));
        using var available = files.Gate.Acquire();
    }

    [Fact]
    public async Task Cleanup_FailureAfterDeadline_PreservesTimeoutAndLogsOnlySafeCode()
    {
        // Arrange
        using var pipeline = new Pipeline();
        var operations = new ExportTests.FaultOperations { Failure = "cancel-write" };
        var logger = new CaptureLog<CsvExporter>();
        using var files = new ExportTests.Fixture(operations, logger);
        await File.WriteAllTextAsync(files.Final, "old complete");
        var service = new ForcesIngestionService(new ForcesTests.FakeClient(), files.Writer, files.Gate, pipeline.Clock);
        // Act
        var task = pipeline.Run(async c => { await service.IngestAsync(c.RequestAborted); });
        await operations.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        operations.Failure = "cleanup";
        pipeline.Clock.Advance(TimeSpan.FromSeconds(120));
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        // Assert
        Assert.Equal(504, pipeline.Context.Response.StatusCode);
        Assert.Contains("operation_timeout", pipeline.Text);
        Assert.Contains("export_cleanup_failed", Assert.Single(logger.Messages));
        Assert.DoesNotContain("sensitive", pipeline.Text + logger.Messages[0]);
        Assert.DoesNotContain(files.Root, logger.Messages[0]);
        Assert.Equal("old complete", await File.ReadAllTextAsync(files.Final));
        Assert.True(File.Exists(Assert.Single(operations.TemporaryPaths)));
        using var available = files.Gate.Acquire();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ErrorResponse_CallerCancelsDuringWrite_DoesNotEscapeOrRetry(bool started)
    {
        // Arrange
        using var caller = new CancellationTokenSource();
        using var pipeline = new Pipeline(caller.Token);
        var stream = new CancelResponse(caller);
        pipeline.Context.Response.Body = stream;
        if (started) pipeline.Context.Features.Set<IHttpResponseFeature>(new StartedResponse(stream));
        // Act
        await pipeline.Run(_ => Task.FromException(new IOException("synthetic-sensitive-body")));
        // Assert
        Assert.Equal(started ? 0 : 1, stream.Writes);
        Assert.Empty(stream.ToArray());
    }

    [Fact]
    public async Task Limits_ReducedConfiguration_IsSnapshottedAndApplied()
    {
        // Arrange
        using var pipeline = new Pipeline();
        pipeline.Context.Request.ContentLength = 33;
        var export = new ExportOptions { MaximumRequestBodyBytes = 32 };
        var police = new PoliceApiOptions { TotalOperationTimeoutSeconds = 1, AttemptTimeoutSeconds = 1 };
        var calls = 0;
        var limits = new IngestionLimitsMiddleware(_ => { calls++; return Task.CompletedTask; },
            Options.Create(export), Options.Create(police), pipeline.Clock);
        export.MaximumRequestBodyBytes = 4096;
        // Act
        await new IngestionErrorMiddleware(limits.InvokeAsync, pipeline.Logger).InvokeAsync(pipeline.Context);
        // Assert
        Assert.Equal(0, calls);
        Assert.Equal(413, pipeline.Context.Response.StatusCode);
    }

    [Fact]
    public async Task Limits_ReducedDeadline_StopsPendingOperation()
    {
        // Arrange
        using var pipeline = new Pipeline();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var police = new PoliceApiOptions { TotalOperationTimeoutSeconds = 1, AttemptTimeoutSeconds = 1 };
        var limits = new IngestionLimitsMiddleware(async c =>
        { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, c.RequestAborted); },
            Options.Create(new ExportOptions()), Options.Create(police), pipeline.Clock);
        police.TotalOperationTimeoutSeconds = 120;
        // Act
        var task = new IngestionErrorMiddleware(limits.InvokeAsync, pipeline.Logger).InvokeAsync(pipeline.Context);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        pipeline.Clock.Advance(TimeSpan.FromSeconds(1));
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        // Assert
        Assert.Equal(504, pipeline.Context.Response.StatusCode);
        Assert.Contains("operation_timeout", pipeline.Text);
    }

    [Fact]
    public async Task Deadline_ConcurrentRequestContexts_DoNotShareAmbientBudget()
    {
        // Arrange
        using var first = new Pipeline();
        using var second = new Pipeline();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = first.Run(async c =>
        {
            first.Clock.Advance(TimeSpan.FromSeconds(100));
            entered.TrySetResult();
            await resume.Task;
            Assert.Equal(TimeSpan.FromSeconds(20), OperationDeadline.Current!.Remaining);
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            // Act / Assert
            await second.Run(_ =>
            {
                Assert.Equal(TimeSpan.FromSeconds(120), OperationDeadline.Current!.Remaining);
                return Task.CompletedTask;
            });
            Assert.Null(OperationDeadline.Current);
        }
        finally { resume.TrySetResult(); }
        await pending;
        Assert.Equal(200, first.Context.Response.StatusCode);
        Assert.Equal(200, second.Context.Response.StatusCode);
    }

    private sealed class CancelResponse(CancellationTokenSource caller) : MemoryStream
    {
        public int Writes;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { Writes++; caller.Cancel(); return ValueTask.FromCanceled(cancellationToken); }
    }
    private sealed class StartedResponse(Stream body) : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = 200;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = body;
        public bool HasStarted => true;
        public void OnStarting(Func<object, Task> callback, object state) { }
        public void OnCompleted(Func<object, Task> callback, object state) { }
    }
    private static ActionContext Action(HttpContext context) => new(context, new RouteData(), new ActionDescriptor(), new ModelStateDictionary());
    private sealed class SizeFeature : IHttpMaxRequestBodySizeFeature { public bool IsReadOnly => false; public long? MaxRequestBodySize { get; set; } }
    private sealed class Jitter : IRetryJitter { public double NextFraction() => 0; }
    private sealed class AllClient : IPoliceApiClient
    {
        public int Calls;
        public Task<IReadOnlyList<ForceDto>> GetForcesAsync(CancellationToken cancellationToken = default) { Calls++; return Task.FromResult<IReadOnlyList<ForceDto>>([]); }
        public Task<IReadOnlyList<CrimeDto>> GetCrimesAsync(LocationMonth request, CancellationToken cancellationToken = default) { Calls++; return Task.FromResult<IReadOnlyList<CrimeDto>>([]); }
        public Task<IReadOnlyList<StopSearchDto>> GetStopSearchesAsync(LocationMonth request, CancellationToken cancellationToken = default) { Calls++; return Task.FromResult<IReadOnlyList<StopSearchDto>>([]); }
    }
    private sealed class CaptureLog<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { Assert.Null(exception); Messages.Add(formatter(state, exception)); }
    }
    private sealed class Pipeline : IDisposable
    {
        private readonly ServiceProvider provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        private readonly MemoryStream response = new();
        public ManualClock Clock { get; } = new();
        public CaptureLog<IngestionErrorMiddleware> Logger { get; } = new();
        public DefaultHttpContext Context { get; }
        public string Text => Encoding.UTF8.GetString(response.ToArray());
        public Pipeline(CancellationToken caller = default)
        {
            Context = new() { RequestServices = provider, TraceIdentifier = "api-trace", RequestAborted = caller };
            Context.Request.Path = "/api/ingestion/forces";
            Context.Response.Body = response;
        }
        public Task Run(RequestDelegate endpoint)
        {
            var limits = new IngestionLimitsMiddleware(endpoint, Options.Create(new ExportOptions()), Options.Create(new PoliceApiOptions()), Clock);
            return new IngestionErrorMiddleware(limits.InvokeAsync, Logger).InvokeAsync(Context);
        }
        public void Dispose() { response.Dispose(); provider.Dispose(); }
    }
}
