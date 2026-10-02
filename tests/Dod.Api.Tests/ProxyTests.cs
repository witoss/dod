using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dod.Api.Tests;

public sealed class ProxyTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "dod-proxy-" + Guid.NewGuid());

    [Theory]
    [InlineData("172.30.47.3", "https", HttpStatusCode.OK)]
    [InlineData("203.0.113.10", "https", HttpStatusCode.InternalServerError)]
    [InlineData("172.30.47.3", "http", HttpStatusCode.InternalServerError)]
    [InlineData("172.30.47.3", null, HttpStatusCode.InternalServerError)]
    public async Task ProductionClaimRequiresHttpsFromTrustedProxy(string peer, string? scheme, HttpStatusCode expected)
    {
        using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:Path"] = Path.Combine(directory, "test.db"),
                ["Tracker:Password"] = "original-owner-secret",
                ["ReverseProxy:KnownProxies:0"] = "172.30.47.3"
            }));
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new ProxyConnection(peer)));
        });
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://dod.example") });
        if (scheme is not null) client.DefaultRequestHeaders.Add("X-Forwarded-Proto", scheme);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        var csrf = await client.GetAsync("/api/account/csrf");
        Assert.Equal(expected, csrf.StatusCode);
        if (expected != HttpStatusCode.OK) return;

        Assert.Contains(csrf.Headers.GetValues("Set-Cookie"), cookie => cookie.Contains("secure", StringComparison.OrdinalIgnoreCase));
        var json = await csrf.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", json.GetProperty("token").GetString());
        var claim = await client.PostAsJsonAsync("/api/account/claim", new
        {
            nickname = "OriginalOwner", password = "new-personal-password", ownerPassword = "original-owner-secret"
        });
        Assert.Equal(HttpStatusCode.NoContent, claim.StatusCode);
        Assert.Contains(claim.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith("dod-session=") && cookie.Contains("secure", StringComparison.OrdinalIgnoreCase));
        var me = await client.GetFromJsonAsync<JsonElement>("/api/account/me");
        Assert.Equal("OriginalOwner", me.GetProperty("nickname").GetString());
    }

    // The browser uses HTTPS, but the actual socket from Caddy to ASP.NET is HTTP.
    private sealed class ProxyConnection(string peer) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
                context.Request.Scheme = "http";
                await nextMiddleware(context);
            });
            next(app);
        };
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
