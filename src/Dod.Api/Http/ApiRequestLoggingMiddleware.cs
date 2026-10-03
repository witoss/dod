using System.Diagnostics;
using Microsoft.AspNetCore.Routing;

namespace Dod.Api.Http;

public sealed class ApiRequestLoggingMiddleware(RequestDelegate next, ILogger<ApiRequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api")) { await next(context); return; }
        var started = Stopwatch.GetTimestamp();
        // Use the server-generated trace, never a client-controlled correlation header.
        var requestId = context.TraceIdentifier;
        context.Response.Headers["X-Request-ID"] = requestId;
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["RequestId"] = requestId });
        var failed = false;
        try { await next(context); }
        catch { failed = true; throw; }
        finally
        {
            var status = failed ? StatusCodes.Status500InternalServerError : context.Response.StatusCode;
            var level = status >= 400 ? LogLevel.Warning : HttpMethods.IsGet(context.Request.Method) ? LogLevel.Debug : LogLevel.Information;
            // Route templates omit actual IDs and dates. Never log bodies, queries, cookies or provider credentials.
            logger.Log(level, "API request {RequestId}: {Method} {Route}; status {StatusCode}; elapsed {ElapsedMs} ms",
                requestId, context.Request.Method, (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched",
                status, Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1));
        }
    }
}
