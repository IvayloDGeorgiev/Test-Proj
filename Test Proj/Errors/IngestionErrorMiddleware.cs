using Microsoft.AspNetCore.Mvc;
using Test_Proj.Export;

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
            var problem = new ProblemDetails
            {
                Status = status,
                Title = status == 409 ? "An ingestion operation is already running." : "Ingestion failed.",
                Type = "about:blank"
            };
            problem.Extensions["code"] = code;
            problem.Extensions["traceId"] = context.TraceIdentifier;
            try
            {
                await context.Response.WriteAsJsonAsync(problem, options: null,
                    contentType: "application/problem+json", cancellationToken: context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        }
    }

    private static (int Status, string Code) Classify(Exception error) => error switch
    {
        PoliceApiException upstream => (upstream.StatusCode, upstream.Code),
        ExportException { Code: "operation_busy", StatusCode: 409 } => (409, "operation_busy"),
        // ExportException accepts strings; never reflect arbitrary codes or statuses.
        ExportException => (500, "export_failed"),
        _ => (500, "internal_error")
    };
}
