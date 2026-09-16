using Microsoft.AspNetCore.Mvc;
using Test_Proj.Export;
using Test_Proj.Validation;

namespace Test_Proj.Errors;

/// <summary>Handles errors before framework diagnostics can log raw exception details.</summary>
public sealed class IngestionErrorMiddleware(RequestDelegate next, ILogger<IngestionErrorMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try { await next(context); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The caller has gone away; do not create an error response.
        }
        catch (Exception error)
        {
            var (status, code) = Classify(error);
            logger.LogWarning("Ingestion failed. TraceId: {TraceId} Code: {Code} Status: {Status}",
                context.TraceIdentifier, code, status);
            if (context.RequestAborted.IsCancellationRequested) return;
            if (context.Response.HasStarted)
            {
                context.Abort();
                return;
            }
            context.Response.Clear();
            context.Response.StatusCode = status;
            ProblemDetails problem = error is RequestValidationException validation
                ? new ValidationProblemDetails(validation.Errors.ToDictionary(pair => pair.Key, pair => pair.Value))
                : new ProblemDetails();
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
        RequestValidationException => (400, "validation_failed"),
        PoliceApiException upstream => (upstream.StatusCode, upstream.Code),
        ExportException { Code: "operation_busy", StatusCode: 409 } => (409, "operation_busy"),
        // ExportException accepts strings; never reflect arbitrary codes or statuses.
        ExportException => (500, "export_failed"),
        _ => (500, "internal_error")
    };
}
