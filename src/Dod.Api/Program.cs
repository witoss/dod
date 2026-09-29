using System.Security.Cryptography;
using System.Text;
using Dod.Api.Entries;
using Dod.Api.Settings;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<EntryStore>();
builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
var app = builder.Build();
var password = app.Configuration["Tracker:Password"];
if (!app.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(password))
    throw new InvalidOperationException("Set Tracker__Password before starting outside Development.");
app.UseExceptionHandler();
await app.Services.GetRequiredService<EntryStore>().InitializeAsync();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.Use(async (context, next) =>
{
    if (context.Request.Path != "/health" && !string.IsNullOrWhiteSpace(password))
    {
        var authorized = false;
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var credentials = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..]));
                authorized = CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(Encoding.UTF8.GetBytes(credentials)),
                    SHA256.HashData(Encoding.UTF8.GetBytes($"tracker:{password}")));
            }
            catch (FormatException) { }
        }
        if (!authorized)
        {
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"DOD\", charset=\"UTF-8\"";
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
    }
    await next(context);
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapEntryEndpoints();
app.MapSettingsEndpoints();
app.MapFallback("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");
app.Run();
public partial class Program;
