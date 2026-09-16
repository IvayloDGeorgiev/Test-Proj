using System.Diagnostics;
using Microsoft.Extensions.Options;
using Test_Proj.Errors;
using Test_Proj.Options;

namespace Test_Proj.Clients.PoliceApi;

public sealed class PoliceApiClient : IPoliceApiClient
{
    private readonly HttpClient http;
    private readonly PoliceApiOptions options;
    private readonly TimeProvider clock;
    private readonly IRetryJitter jitter;
    private readonly ILogger<PoliceApiClient> logger;

    public PoliceApiClient(HttpClient http, IOptions<PoliceApiOptions> options, TimeProvider clock,
        IRetryJitter jitter, ILogger<PoliceApiClient> logger)
    {
        var value = options.Value;
        if (new PoliceApiOptionsValidator().Validate(null, value).Failed)
            throw new InvalidOperationException("Police API configuration is invalid.");
        this.http = http;
        // Snapshot configuration so later mutation cannot change the trusted origin or limits.
        this.options = new PoliceApiOptions
        {
            BaseUrl = value.BaseUrl, AttemptTimeoutSeconds = value.AttemptTimeoutSeconds,
            TotalOperationTimeoutSeconds = value.TotalOperationTimeoutSeconds, MaximumAttempts = value.MaximumAttempts,
            MaximumResponseBytes = value.MaximumResponseBytes, MaximumRecords = value.MaximumRecords
        };
        this.clock = clock;
        this.jitter = jitter;
        this.logger = logger;
    }

    public Task<IReadOnlyList<ForceDto>> GetForcesAsync(CancellationToken cancellationToken = default) =>
        GetArrayAsync("forces", "forces", (ForceDto force) => force.IsValid(), cancellationToken);

    public Task<IReadOnlyList<CrimeDto>> GetCrimesAsync(Test_Proj.Validation.LocationMonth request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return GetArrayAsync("crimes-street/all-crime?" + request.ToQueryString(), "crimes",
            (CrimeDto crime) => crime.IsValid(request.Month), cancellationToken);
    }

    public Task<IReadOnlyList<StopSearchDto>> GetStopSearchesAsync(Test_Proj.Validation.LocationMonth request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return GetArrayAsync("stops-street?" + request.ToQueryString(), "stop-searches",
            (StopSearchDto stop) => stop.IsValid(), cancellationToken);
    }

    // Code-owned paths only. Dataset methods added in their own stages call this single retry owner.
    private async Task<IReadOnlyList<T>> GetArrayAsync<T>(string path, string dataset, Func<T, bool> validate,
        CancellationToken caller) where T : class
    {
        caller.ThrowIfCancellationRequested();
        var started = clock.GetTimestamp();
        var budget = TimeSpan.FromSeconds(options.TotalOperationTimeoutSeconds);
        using var totalTimer = new CancellationTokenSource(budget, clock);
        using var total = CancellationTokenSource.CreateLinkedTokenSource(caller, totalTimer.Token);
        var attempt = 0;
        try
        {
            for (attempt = 1; attempt <= options.MaximumAttempts; attempt++)
            {
                total.Token.ThrowIfCancellationRequested();
                PoliceApiFailure failure;
                TimeSpan? retryAfter = null;
                using (var timer = new CancellationTokenSource(TimeSpan.FromSeconds(options.AttemptTimeoutSeconds), clock))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(total.Token, timer.Token))
                {
                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(options.BaseUrl), path));
                        request.Headers.Accept.ParseAdd("application/json");
                        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
                        var status = (int)response.StatusCode;
                        logger.LogInformation("Police API {Dataset} trace {TraceId} attempt {Attempt} status class {StatusClass}",
                            dataset, Activity.Current?.TraceId.ToString(), attempt, status / 100);
                        if (status == 200)
                        {
                            var records = await PoliceArrayReader.ReadAsync(response.Content, options.MaximumResponseBytes,
                                options.MaximumRecords, validate, linked.Token);
                            linked.Token.ThrowIfCancellationRequested();
                            LogResult(dataset, started, attempt, "success", records.Count);
                            return records;
                        }
                        if (status is not (408 or 429 or 500 or 502 or 503 or 504))
                            throw new PoliceApiException(PoliceApiFailure.UpstreamFailure);
                        failure = status == 429 ? PoliceApiFailure.RateLimited : PoliceApiFailure.UpstreamFailure;
                        var header = response.Headers.RetryAfter;
                        if (header?.Delta is { } delta && delta >= TimeSpan.Zero) retryAfter = delta;
                        else if (header?.Date is { } date)
                        {
                            var now = clock.GetUtcNow();
                            if (date > now) retryAfter = date - now;
                        }
                    }
                    catch (OperationCanceledException) when (!total.IsCancellationRequested)
                    {
                        failure = PoliceApiFailure.Timeout;
                    }
                    catch (HttpRequestException exception) when (exception.HttpRequestError is
                        HttpRequestError.Unknown or HttpRequestError.NameResolutionError or
                        HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded)
                    {
                        failure = PoliceApiFailure.UpstreamFailure;
                    }
                    catch (HttpRequestException)
                    {
                        // Certificate, authentication, protocol and configuration errors are not transient.
                        throw new PoliceApiException(PoliceApiFailure.UpstreamFailure);
                    }
                    catch (IOException)
                    {
                        failure = PoliceApiFailure.UpstreamFailure;
                    }
                }
                total.Token.ThrowIfCancellationRequested();
                if (attempt == options.MaximumAttempts) throw new PoliceApiException(failure);
                var backoff = TimeSpan.FromMilliseconds(Math.Min(5000,
                    500 * Math.Pow(2, attempt - 1) + 250 * Math.Clamp(jitter.NextFraction(), 0, 1)));
                var delay = retryAfter is { } minimum && minimum > backoff ? minimum : backoff;
                var remaining = budget - clock.GetElapsedTime(started);
                if (OperationDeadline.Current is { } operation && operation.Remaining < remaining)
                    remaining = operation.Remaining;
                if (delay >= remaining)
                    throw new PoliceApiException(PoliceApiFailure.RetryBudget);
                await Task.Delay(delay, clock, total.Token);
            }
            throw new UnreachableException();
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested)
        {
            LogResult(dataset, started, attempt, "cancelled", 0);
            throw new OperationCanceledException(caller);
        }
        catch (OperationCanceledException)
        {
            LogResult(dataset, started, attempt, "upstream_timeout", 0);
            throw new PoliceApiException(PoliceApiFailure.Timeout);
        }
        catch (PoliceApiException exception)
        {
            caller.ThrowIfCancellationRequested();
            LogResult(dataset, started, attempt, exception.Code, 0);
            throw;
        }
    }

    private void LogResult(string dataset, long started, int attempt, string result, int count) =>
        logger.LogInformation("Police API {Dataset} trace {TraceId} attempt {Attempt} count {Count} elapsed ms {ElapsedMs} result {Result}",
            dataset, Activity.Current?.TraceId.ToString(), attempt, count, clock.GetElapsedTime(started).TotalMilliseconds, result);
}
