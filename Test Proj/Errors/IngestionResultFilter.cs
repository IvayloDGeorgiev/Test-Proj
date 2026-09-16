using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Test_Proj.Contracts.Responses;

namespace Test_Proj.Errors;

public sealed class IngestionResultFilter(TimeProvider clock, ILogger<IngestionResultFilter> logger)
    : IAsyncActionFilter, IAlwaysRunResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        // Framework-generated binding/media-type errors must follow the same safe contract.
        if (context.Result is ObjectResult { StatusCode: 400 or 415 } error)
        {
            var status = error.StatusCode.Value;
            ProblemDetails problem = status == 400
                ? new ValidationProblemDetails(new Dictionary<string, string[]> { ["request"] = ["Supply a valid request."] })
                : new ProblemDetails();
            problem.Status = status;
            problem.Title = "Request rejected.";
            problem.Type = "about:blank";
            problem.Extensions["code"] = status == 415 ? "unsupported_media_type" : "validation_failed";
            problem.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
            error.Value = problem;
            error.DeclaredType = problem.GetType();
            error.ContentTypes.Clear();
            error.ContentTypes.Add("application/problem+json");
        }
    }

    public void OnResultExecuted(ResultExecutedContext context) { }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var started = clock.GetTimestamp();
        var executed = await next();
        if (executed.Exception is null && executed.Result is ObjectResult { Value: IngestionResult result })
        {
            // The service has published. Only caller cancellation governs response serialization now.
            if (OperationDeadline.Current is { } deadline) context.HttpContext.RequestAborted = deadline.Caller;
            logger.LogInformation("Ingestion {Dataset} trace {TraceId} count {Count} elapsed ms {ElapsedMs} result {Result}",
                result.Dataset, context.HttpContext.TraceIdentifier, result.RecordCount,
                clock.GetElapsedTime(started).TotalMilliseconds, "published");
        }
    }
}
