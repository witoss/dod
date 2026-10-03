using System.Threading.RateLimiting;
using Dod.Api.Accounts;
using Dod.Api.Entries;
using Dod.Api.Settings;
using Dod.Api.Social;
using Dod.Api.Motivation;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);
var allowHttp = builder.Environment.IsDevelopment() || builder.Configuration.GetValue<bool>("Authentication:AllowHttp");
builder.Services.AddProblemDetails();
builder.Services.AddHttpContextAccessor();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    // Only configured proxy addresses may describe the browser's original scheme.
    foreach (var proxy in builder.Configuration.GetSection("ReverseProxy:KnownProxies").GetChildren())
        options.KnownProxies.Add(System.Net.IPAddress.Parse(proxy.Value!));
});
builder.Services.AddSingleton<EntryStore>();
builder.Services.AddSingleton<SocialStore>();
builder.Services.AddSingleton<MotivationStore>();
builder.Services.AddSingleton(TimeProvider.System);
var dataDirectory = Path.GetDirectoryName(Path.GetFullPath(builder.Configuration["Storage:Path"] ?? "data/dod.db"))!;
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys"))).SetApplicationName("DOD");
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "dod-session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    // Production is served through HTTPS at Caddy, even though the internal hop is HTTP.
    options.Cookie.SecurePolicy = allowHttp ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromDays(7);
    options.SlidingExpiration = false;
    options.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.SecurePolicy = allowHttp ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    // Global account-operation budget also works behind the proxy without trusting forwarded IP headers.
    options.AddPolicy("accounts", _ => RateLimitPartition.GetFixedWindowLimiter("accounts", _ => new FixedWindowRateLimiterOptions
    { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
var app = builder.Build();
app.UseExceptionHandler();
app.UseForwardedHeaders();
await app.Services.GetRequiredService<EntryStore>().InitializeAsync();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api"))
    {
        ctx.Response.Headers.CacheControl = "no-store";
        if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method) && !HttpMethods.IsOptions(ctx.Request.Method))
        {
            try { await ctx.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(ctx); }
            catch (AntiforgeryValidationException) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { detail = "Session verification failed. Refresh and try again." }); return; }
        }
    }
    await next(ctx);
});
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapAccountEndpoints();
app.MapEntryEndpoints();
app.MapSettingsEndpoints();
app.MapSocialEndpoints();
app.MapMotivationEndpoints();
app.MapFallback("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");
app.Run();
public partial class Program;
