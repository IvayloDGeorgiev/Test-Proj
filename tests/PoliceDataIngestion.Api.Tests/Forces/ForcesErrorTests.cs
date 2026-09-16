using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Test_Proj.Errors;
using Test_Proj.Export;

namespace PoliceDataIngestion.Api.Tests.Forces;

public sealed class ForcesErrorTests
{
    public static TheoryData<Exception, int, string> Failures => new()
    {
        { new PoliceApiException(PoliceApiFailure.InvalidPayload), 502, "upstream_invalid_payload" },
        { new PoliceApiException(PoliceApiFailure.ResponseLimit), 502, "upstream_limit_exceeded" },
        { new PoliceApiException(PoliceApiFailure.UpstreamFailure), 502, "upstream_failure" },
        { new PoliceApiException(PoliceApiFailure.RateLimited), 503, "upstream_rate_limited" },
        { new PoliceApiException(PoliceApiFailure.RetryBudget), 503, "upstream_retry_budget" },
        { new PoliceApiException(PoliceApiFailure.Timeout), 504, "upstream_timeout" },
        { new ExportException("operation_busy", 409), 409, "operation_busy" },
        { new ExportException("unsafe_export_path"), 500, "export_failed" },
        { new ExportException("C:\\sensitive\\secret-token", 200), 500, "export_failed" },
        { new IOException("C:\\sensitive\\secret-token"), 500, "internal_error" },
        { new InvalidOperationException("upstream-body secret-token"), 500, "internal_error" },
        { new OperationCanceledException("secret-token"), 500, "internal_error" }
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Errors_ClassifiedFailure_ReturnsSafeProblemAndSafeLog(Exception failure, int status, string code)
    {
        // Arrange
        using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var body = new MemoryStream();
        var context = new DefaultHttpContext { RequestServices = provider, TraceIdentifier = "trace-forces" };
        context.Response.Body = body;
        var logger = new CaptureLogger();
        var middleware = new IngestionErrorMiddleware(_ => Task.FromException(failure), logger);
        // Act
        await middleware.InvokeAsync(context);
        // Assert
        Assert.Equal(status, context.Response.StatusCode);
        Assert.StartsWith("application/problem+json", context.Response.ContentType);
        body.Position = 0;
        using var problem = await JsonDocument.ParseAsync(body);
        Assert.Equal(status, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("trace-forces", problem.RootElement.GetProperty("traceId").GetString());
        Assert.Equal("about:blank", problem.RootElement.GetProperty("type").GetString());
        Assert.False(problem.RootElement.TryGetProperty("success", out _));
        var output = problem.RootElement.GetRawText() + string.Join("", logger.Messages);
        Assert.DoesNotContain("secret-token", output);
        Assert.DoesNotContain("sensitive", output);
        Assert.DoesNotContain("upstream-body", output);
        Assert.DoesNotContain("StackTrace", output);
        Assert.Single(logger.Messages);
        Assert.Contains(code, logger.Messages[0]);
        Assert.All(logger.Exceptions, Assert.Null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Errors_DisconnectedCaller_DoesNotAttemptResponse(bool unrelatedFailure)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var context = new DefaultHttpContext { RequestAborted = cancellation.Token };
        var body = new RejectWriteStream();
        context.Response.Body = body;
        Exception failure = unrelatedFailure ? new IOException("private") : new OperationCanceledException(cancellation.Token);
        var middleware = new IngestionErrorMiddleware(_ => Task.FromException(failure), new CaptureLogger());
        // Act
        await middleware.InvokeAsync(context);
        // Assert
        Assert.Equal(0, body.WriteAttempts);
        Assert.Null(context.Response.ContentType);
        Assert.Empty(context.Response.Headers);
    }

    [Fact]
    public async Task Errors_Success_PreservesResponseWithoutLoggingFailure()
    {
        // Arrange
        var context = new DefaultHttpContext();
        using var body = new MemoryStream();
        context.Response.Body = body;
        var logger = new CaptureLogger();
        var middleware = new IngestionErrorMiddleware(c => c.Response.WriteAsync("published"), logger);
        // Act
        await middleware.InvokeAsync(context);
        // Assert
        Assert.Equal("published", System.Text.Encoding.UTF8.GetString(body.ToArray()));
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Empty(logger.Messages);
    }

    private sealed class CaptureLogger : ILogger<IngestionErrorMiddleware>
    {
        public List<string> Messages { get; } = [];
        public List<Exception?> Exceptions { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { Messages.Add(formatter(state, exception)); Exceptions.Add(exception); }
    }
    private sealed class RejectWriteStream : MemoryStream
    {
        public int WriteAttempts { get; private set; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { WriteAttempts++; throw new InvalidOperationException("Response attempted after disconnection."); }
        public override void Write(byte[] buffer, int offset, int count)
        { WriteAttempts++; throw new InvalidOperationException("Response attempted after disconnection."); }
    }
}
