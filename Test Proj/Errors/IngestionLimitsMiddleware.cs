using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Test_Proj.Options;

namespace Test_Proj.Errors;

public sealed class IngestionLimitsMiddleware(RequestDelegate next, IOptions<ExportOptions> export,
    IOptions<PoliceApiOptions> police, TimeProvider clock)
{
    private readonly int maximumBytes = export.Value.MaximumRequestBodyBytes;
    private readonly TimeSpan budget = TimeSpan.FromSeconds(police.Value.TotalOperationTimeoutSeconds);

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api/ingestion") &&
            !context.Request.Path.StartsWithSegments("/api/sync") &&
            !context.Request.Path.StartsWithSegments("/api/data")) { await next(context); return; }
        var caller = context.RequestAborted;
        var originalBody = context.Request.Body;
        var previous = OperationDeadline.Current;
        using var timer = new CancellationTokenSource(budget, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, timer.Token);
        using var bounded = new MemoryStream();
        OperationDeadline.Current = new(clock, budget, caller);
        context.RequestAborted = linked.Token;
        try
        {
            caller.ThrowIfCancellationRequested();
            var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = maximumBytes;
            if (context.Request.ContentLength > maximumBytes) throw new RequestBodyLimitException();
            // Read at most limit + 1, including chunked/unknown-length bodies, before any side effects.
            var buffer = new byte[maximumBytes + 1];
            var count = 0;
            while (count < buffer.Length)
            {
                var read = await originalBody.ReadAsync(buffer.AsMemory(count), linked.Token);
                if (read == 0) break;
                count += read;
            }
            if (count > maximumBytes) throw new RequestBodyLimitException();
            await bounded.WriteAsync(buffer.AsMemory(0, count), linked.Token);
            bounded.Position = 0;
            context.Request.Body = bounded;
            await next(context);
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested && timer.IsCancellationRequested)
        { throw new OperationTimeoutException(); }
        finally
        {
            context.RequestAborted = caller;
            context.Request.Body = originalBody;
            OperationDeadline.Current = previous;
        }
    }
}
