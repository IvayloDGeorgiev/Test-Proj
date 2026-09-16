namespace Test_Proj.Errors;

// Async-local state follows one request, including the typed HttpClient, without sharing budgets.
public sealed class OperationDeadline(TimeProvider clock, TimeSpan budget, CancellationToken caller)
{
    private static readonly AsyncLocal<OperationDeadline?> Ambient = new();
    private readonly long started = clock.GetTimestamp();
    public static OperationDeadline? Current { get => Ambient.Value; internal set => Ambient.Value = value; }
    public CancellationToken Caller { get; } = caller;
    public TimeSpan Remaining => budget - clock.GetElapsedTime(started);
}

public sealed class OperationTimeoutException() : Exception("Ingestion operation timed out.");
public sealed class RequestBodyLimitException() : Exception("Request body is too large.");
