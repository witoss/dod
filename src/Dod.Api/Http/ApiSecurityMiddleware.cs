using Microsoft.AspNetCore.Antiforgery;

namespace Dod.Api.Http;

public sealed class ApiSecurityMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)
                && !HttpMethods.IsOptions(context.Request.Method))
            {
                try { await antiforgery.ValidateRequestAsync(context); }
                catch (AntiforgeryValidationException)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsJsonAsync(new { detail = "Session verification failed. Refresh and try again." });
                    return;
                }
            }
        }
        await next(context);
    }
}
