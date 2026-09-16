namespace Test_Proj.Errors;

public sealed class SyncOriginException() : Exception("Sync request rejected.");

public sealed class SyncOriginMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/api/sync") && HttpMethods.IsPost(context.Request.Method))
        {
            // Browser cross-origin forms cannot supply this header; no CORS policy permits it.
            var origin = context.Request.Headers.Origin;
            if (context.Request.Headers["X-Police-Sync"] != "1" ||
                (origin.Count != 0 && origin != $"{context.Request.Scheme}://{context.Request.Host}"))
                throw new SyncOriginException();
        }
        return next(context);
    }
}
