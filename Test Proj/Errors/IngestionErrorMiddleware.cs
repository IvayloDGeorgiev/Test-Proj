using Microsoft.AspNetCore.Mvc;
using Test_Proj.Export;
using Test_Proj.Validation;

namespace Test_Proj.Errors;

/// <summary>Handles errors before framework diagnostics can log raw exception details.</summary>
public sealed class IngestionErrorMiddleware(RequestDelegate next, ILogger<IngestionErrorMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { await next(context); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The caller has gone away; do not create an error response.
            logger.LogInformation("Ingestion trace {TraceId} elapsed ms {ElapsedMs} result {Result}",
                context.TraceIdentifier, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, "caller_cancelled");
        }
        catch (Exception error)
        {
            var (status, code) = Classify(error);
            var dataset = context.Request.Path.Value?.TrimEnd('/').ToLowerInvariant() switch
            {
                "/api/ingestion/forces" => "forces",
                "/api/ingestion/crimes" => "crimes",
                "/api/ingestion/stop-searches" => "stop-searches",
                _ => "unknown"
            };
            logger.LogWarning("Ingestion {Dataset} trace {TraceId} result {Code} status {Status} status class {StatusClass} elapsed ms {ElapsedMs}",
                dataset, context.TraceIdentifier, code, status, status / 100,
                System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            if (context.RequestAborted.IsCancellationRequested) return;
            if (context.Response.HasStarted)
            {
                context.Abort();
                return;
            }
            context.Response.Clear();
            context.Response.StatusCode = status;
            ProblemDetails problem = error switch
            {
                RequestValidationException validation => SafeValidation(validation),
                BadHttpRequestException when status == 400 => new ValidationProblemDetails(
                    new Dictionary<string, string[]> { ["request"] = ["Supply a valid request."] }),
                _ => new ProblemDetails()
            };
            problem.Status = status;
            problem.Title = status switch
            {
                400 => "Request validation failed.",
                409 => "An ingestion operation is already running.",
                _ => "Ingestion failed."
            };
            problem.Type = "about:blank";
            problem.Extensions["code"] = code;
            problem.Extensions["traceId"] = context.TraceIdentifier;
            try
            {
                if (problem is ValidationProblemDetails invalid)
                    await context.Response.WriteAsJsonAsync(invalid, options: null,
                        contentType: "application/problem+json", cancellationToken: context.RequestAborted);
                else
                    await context.Response.WriteAsJsonAsync(problem, options: null,
                        contentType: "application/problem+json", cancellationToken: context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        }
    }

    private static (int Status, string Code) Classify(Exception error) => error switch
    {
        SyncOriginException => (403, "sync_origin_rejected"),
        Test_Proj.Persistence.DatabaseConfigurationException => (503, "database_not_configured"),
        Test_Proj.Persistence.DatabaseOperationException => (503, "database_unavailable"),
        Test_Proj.Persistence.DatabaseTimeoutException => (504, "database_timeout"),
        RequestBodyLimitException => (413, "request_body_too_large"),
        BadHttpRequestException { StatusCode: 413 } => (413, "request_body_too_large"),
        BadHttpRequestException => (400, "validation_failed"),
        OperationTimeoutException => (504, "operation_timeout"),
        RequestValidationException => (400, "validation_failed"),
        PoliceApiException upstream => (upstream.StatusCode, upstream.Code),
        ExportException { Code: "operation_busy", StatusCode: 409 } => (409, "operation_busy"),
        // ExportException accepts strings; never reflect arbitrary codes or statuses.
        ExportException => (500, "export_failed"),
        _ => (500, "internal_error")
    };

    private static ValidationProblemDetails SafeValidation(RequestValidationException validation)
    {
        var errors = new Dictionary<string, string[]>();
        foreach (var key in new[] { "latitude", "longitude", "month" })
            if (validation.Errors.ContainsKey(key)) errors[key] = ["Supply a valid " + key + "."];
        if (errors.Count == 0) errors["request"] = ["Supply a valid request."];
        return new(errors);
    }
}
