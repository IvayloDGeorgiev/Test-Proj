using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Test_Proj.Export;
using static PoliceDataIngestion.Api.Tests.Integration.HostContractsTests;

namespace PoliceDataIngestion.Api.Tests.Integration;

public sealed class HostCancellationTests
{
    public static IEnumerable<object[]> Contenders()
    {
        foreach (var active in Routes)
        foreach (var contender in Routes)
            yield return [active, contender];
    }

    [Theory]
    [MemberData(nameof(Contenders))]
    public async Task Admission_AnotherRouteRetrieving_Returns409ImmediatelyAndThenAccepts(string active, string contender)
    {
        // Arrange
        var entered = Signal();
        var release = Signal();
        await using var host = new IngestionHost(async (call, _, token) =>
        {
            if (call == 1) { entered.TrySetResult(); await release.Task.WaitAsync(token); }
            return IngestionHost.Response("[]");
        });
        using var client = host.Client();
        var pending = Post(client, active);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            // Act
            using var rejected = await Post(client, contender).WaitAsync(TimeSpan.FromSeconds(5));
            // Assert
            await AssertProblem(rejected, 409, "operation_busy");
            Assert.Equal(1, host.Handler.Calls);
            Assert.Equal(0, host.Files.Creates);
            Assert.False(pending.IsCompleted);
        }
        finally { release.TrySetResult(); }
        using var first = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var next = await Post(client, contender);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.Equal(2, host.Handler.Calls);
        Assert.Equal(2, host.Files.Publications);
    }

    [Theory]
    [InlineData("forces", "deadline")]
    [InlineData("crimes", "deadline")]
    [InlineData("stop-searches", "deadline")]
    [InlineData("forces", "attempt")]
    [InlineData("crimes", "attempt")]
    [InlineData("stop-searches", "attempt")]
    [InlineData("forces", "caller")]
    [InlineData("crimes", "caller")]
    [InlineData("stop-searches", "caller")]
    public async Task Cancellation_UpstreamPending_DistinguishesAttemptDeadlineAndCaller(string route, string cause)
    {
        // Arrange
        var entered = Signal();
        var cancelled = Signal();
        await using var host = new IngestionHost(async (_, _, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { if (token.IsCancellationRequested) cancelled.TrySetResult(); }
            return IngestionHost.Response("[]");
        });
        using var client = host.Client();
        using var caller = new CancellationTokenSource();
        var pending = Post(client, route, caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Act
        if (cause == "caller") caller.Cancel();
        else host.Clock.Advance(TimeSpan.FromSeconds(cause == "attempt" ? 31 : 121));
        // Assert
        if (cause == "caller")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
            await host.Logs.CallerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain(host.Logs.Entries, e => e.Message.Contains("operation_timeout") || e.Message.Contains("published"));
        }
        else
        {
            using var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            await AssertProblem(response, 504, cause == "attempt" ? "upstream_timeout" : "operation_timeout");
        }
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, host.Handler.Calls);
        Assert.Equal(0, host.Files.Creates);
        Assert.False(Directory.Exists(host.OutputRoot));
        using var lease = host.Services.GetRequiredService<OperationLease>().Acquire();
        AssertSafeLogs(host);
    }

    [Theory]
    [InlineData("forces", false)]
    [InlineData("crimes", false)]
    [InlineData("stop-searches", false)]
    [InlineData("forces", true)]
    [InlineData("crimes", true)]
    [InlineData("stop-searches", true)]
    public async Task Cancellation_DuringFileWrite_PreservesOldFileCleansTempAndReleasesGlobalAdmission(string route, bool cancelCaller)
    {
        // Arrange: real OS stream wrapped only to hold its first cancellable write.
        await using var host = new IngestionHost();
        var entered = Signal();
        host.Files.Create = path => new HeldWriteStream(new AtomicFileOperations().CreateTemporary(path), entered);
        Directory.CreateDirectory(host.OutputRoot);
        var final = Path.Combine(host.OutputRoot, Filename(route));
        await File.WriteAllTextAsync(final, "old-complete-export");
        using var client = host.Client();
        using var caller = new CancellationTokenSource();
        var pending = Post(client, route, caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var other in Routes)
        {
            using var busy = await Post(client, other).WaitAsync(TimeSpan.FromSeconds(5));
            await AssertProblem(busy, 409, "operation_busy");
        }
        // Act
        if (cancelCaller) caller.Cancel();
        else host.Clock.Advance(TimeSpan.FromSeconds(121));
        // Assert
        if (cancelCaller)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
            await host.Logs.CallerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        else
        {
            using var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            await AssertProblem(response, 504, "operation_timeout");
        }
        Assert.Equal("old-complete-export", await File.ReadAllTextAsync(final));
        Assert.Single(Directory.GetFiles(host.OutputRoot));
        Assert.Equal(1, host.Handler.Calls);
        Assert.Equal(1, host.Files.Creates);
        Assert.Equal(0, host.Files.Publications);
        using var lease = host.Services.GetRequiredService<OperationLease>().Acquire();
        AssertSafeLogs(host);
    }

    [Theory]
    [InlineData("forces")]
    [InlineData("crimes")]
    [InlineData("stop-searches")]
    public async Task Publication_DeadlineExpiresAfterAtomicCommit_StillSerializesCompleteSuccess(string route)
    {
        // Arrange
        await using var host = new IngestionHost((_, _, _) => Task.FromResult(IngestionHost.Response(Payload(route))));
        host.Files.AfterPublish = () => host.Clock.Advance(TimeSpan.FromSeconds(121));
        using var client = host.Client();
        // Act
        using var response = await Post(client, route);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        // Assert: the real MVC action filter must restore the caller token before formatter writes.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Equal(1, json.GetProperty("recordCount").GetInt32());
        Assert.Equal(Filename(route), json.GetProperty("filename").GetString());
        Assert.Equal(Header(route) + Row(route), await File.ReadAllTextAsync(Path.Combine(host.OutputRoot, Filename(route))));
        Assert.Single(Directory.GetFiles(host.OutputRoot));
        Assert.Equal(1, host.Handler.Calls);
        Assert.Equal(1, host.Files.Publications);
        Assert.DoesNotContain(host.Logs.Entries, e => e.Message.Contains("operation_timeout"));
        using var lease = host.Services.GetRequiredService<OperationLease>().Acquire();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Input_PendingRead_ConsumesOperationBudgetAndPropagatesCallerCancellation(bool cancelCaller)
    {
        // Arrange
        await using var host = new IngestionHost();
        using var client = host.Client();
        using var body = new PoliceApi.BlockingStream();
        using var caller = new CancellationTokenSource();
        var pending = host.Server.SendAsync(context =>
        {
            context.Request.Method = "POST";
            context.Request.Scheme = "https";
            context.Request.Host = new HostString("localhost");
            context.Request.Path = "/api/ingestion/crimes";
            context.Request.ContentType = "application/json";
            context.Request.Body = body;
        }, caller.Token);
        await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Act
        if (cancelCaller) caller.Cancel();
        else host.Clock.Advance(TimeSpan.FromSeconds(121));
        // Assert
        if (cancelCaller)
        {
            // SendAsync exposes the server context rather than HttpClient's cancelled task.
            var context = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            await host.Logs.CallerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(context.RequestAborted.IsCancellationRequested);
            using var reader = new StreamReader(context.Response.Body);
            await Assert.ThrowsAsync<IOException>(() => reader.ReadToEndAsync());
            Assert.Null(context.Response.ContentType);
        }
        else
        {
            var context = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(504, context.Response.StatusCode);
            using var reader = new StreamReader(context.Response.Body);
            var json = JsonDocument.Parse(await reader.ReadToEndAsync()).RootElement;
            Assert.Equal("operation_timeout", json.GetProperty("code").GetString());
        }
        Assert.Equal(0, host.Handler.Calls);
        Assert.Equal(0, host.Files.Creates);
    }

    [Fact]
    public async Task Retry_InputConsumesBudget_DoesNotAdmitDelayThatOnlyFitsRetrievalBudget()
    {
        // Arrange: input consumes 60 seconds; a 70-second minimum fits retrieval's 120, not the remaining 60.
        await using var host = new IngestionHost((_, _, _) =>
        {
            var response = IngestionHost.Response("[]", 429);
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(70));
            return Task.FromResult(response);
        });
        host.Settings["PoliceApi:MaximumAttempts"] = "3";
        using var client = host.Client();
        using var body = new TimedInput(System.Text.Encoding.UTF8.GetBytes(RequestJson), host.Clock);
        // Act
        var context = await host.Server.SendAsync(context =>
        {
            context.Request.Method = "POST";
            context.Request.Scheme = "https";
            context.Request.Host = new HostString("localhost");
            context.Request.Path = "/api/ingestion/crimes";
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = body.Length;
            context.Request.Body = body;
        }).WaitAsync(TimeSpan.FromSeconds(5));
        // Assert
        Assert.Equal(503, context.Response.StatusCode);
        using var reader = new StreamReader(context.Response.Body);
        using var document = JsonDocument.Parse(await reader.ReadToEndAsync());
        Assert.Equal("upstream_retry_budget", document.RootElement.GetProperty("code").GetString());
        Assert.Equal(1, host.Handler.Calls);
        Assert.Equal(0, host.Files.Creates);
    }

    private sealed class TimedInput(byte[] bytes, PoliceApi.ManualClock clock) : MemoryStream(bytes)
    {
        private bool advanced;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!advanced) { advanced = true; clock.Advance(TimeSpan.FromSeconds(60)); }
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class HeldWriteStream(Stream inner, TaskCompletionSource entered) : Stream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); GC.SuppressFinalize(this); }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
