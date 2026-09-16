using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Test_Proj.Clients.PoliceApi;
using Test_Proj.Options;

namespace PoliceDataIngestion.Api.Tests.PoliceApi;

internal sealed class ManualClock : TimeProvider
{
    private readonly List<ManualTimer> timers = [];
    private readonly Channel<TimeSpan> scheduled = Channel.CreateUnbounded<TimeSpan>();
    private long ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => ticks;
    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero).AddTicks(ticks);
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (timers) timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }
    public async Task<TimeSpan> WaitForBackoffAsync()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var due = await scheduled.Reader.ReadAsync(guard.Token);
            if (due <= TimeSpan.FromSeconds(5)) return due;
        }
    }
    public void Advance(TimeSpan duration)
    {
        ticks += duration.Ticks;
        ManualTimer[] snapshot;
        lock (timers) snapshot = timers.ToArray();
        foreach (var timer in snapshot) timer.Fire(ticks);
    }
    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        private long due = long.MaxValue;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.ticks + dueTime.Ticks;
            clock.scheduled.Writer.TryWrite(dueTime);
            return true;
        }
        public void Fire(long now)
        {
            if (due > now) return;
            due = long.MaxValue;
            callback(state);
        }
        public void Dispose() => due = long.MaxValue;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

internal sealed class ScriptedHandler(Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
    : HttpMessageHandler
{
    public int Calls { get; private set; }
    public List<Uri> Uris { get; } = [];
    public List<CancellationToken> Tokens { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        Uris.Add(request.RequestUri!);
        Tokens.Add(cancellationToken);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("application/json", Assert.Single(request.Headers.Accept).MediaType);
        return send(Calls, request, cancellationToken);
    }
}

internal sealed class CapturingLogger : ILogger<PoliceApiClient>
{
    public List<string> Messages { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Assert.Null(exception);
        Messages.Add(formatter(state, exception));
    }
}

internal sealed class TransportFixture : IDisposable
{
    public ManualClock Clock { get; } = new();
    public CapturingLogger Logger { get; } = new();
    public ScriptedHandler Handler { get; }
    public PoliceApiClient Client { get; }
    private readonly HttpClient http;
    public TransportFixture(Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        PoliceApiOptions? options = null, double jitter = 0)
    {
        Handler = new(send);
        http = new(Handler) { Timeout = Timeout.InfiniteTimeSpan };
        Client = new(http, Microsoft.Extensions.Options.Options.Create(options ?? new()), Clock, new FixedJitter(jitter), Logger);
    }
    public static HttpResponseMessage Response(int status = 200, string body = "[]") =>
        new((System.Net.HttpStatusCode)status) { Content = new StringContent(body) };
    public async Task AdvanceBackoffAsync() => Clock.Advance(await Clock.WaitForBackoffAsync());
    public void Dispose() => http.Dispose();
    private sealed class FixedJitter(double fraction) : IRetryJitter { public double NextFraction() => fraction; }
}

internal sealed class TrackingContent(string body) : StringContent(body)
{
    public bool Disposed { get; private set; }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
}

internal sealed class BlockingStream : Stream
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Disposed { get; private set; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Entered.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
    }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
