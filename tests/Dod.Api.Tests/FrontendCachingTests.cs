using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Dod.Api.Tests;

public sealed class FrontendCachingTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "dod-caching-" + Guid.NewGuid());
    private readonly string webRoot;

    public FrontendCachingTests()
    {
        webRoot = Path.Combine(directory, "wwwroot");
        Directory.CreateDirectory(Path.Combine(webRoot, "assets"));
        File.WriteAllText(Path.Combine(webRoot, "index.html"), "<html>release one</html>");
        File.SetLastWriteTimeUtc(Path.Combine(webRoot, "index.html"), new DateTime(2020, 1, 1));
        File.WriteAllText(Path.Combine(webRoot, "assets", "index-a1b2c3d4.js"), "console.log('release one');");
        File.WriteAllText(Path.Combine(webRoot, "assets", "index-e5f6g7h8.css"), "body { color: green; }");
        File.WriteAllText(Path.Combine(webRoot, "assets", "config.js"), "console.log('unversioned');");
    }
    private WebApplicationFactory<Program> App() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Production");
        builder.UseWebRoot(webRoot);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        { ["Storage:Path"] = Path.Combine(directory, "test.db") }));
    });

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/journal")]
    public async Task HtmlRevalidatesAndNormalConditionalRefreshFindsNewRelease(string path)
    {
        string previousTag;
        using (var app = App())
        using (var client = app.CreateClient())
        {
            var first = await client.GetAsync(path); first.EnsureSuccessStatusCode();
            Assert.True(first.Headers.CacheControl!.NoCache);
            Assert.True(first.Headers.CacheControl.MustRevalidate);
            Assert.Equal(TimeSpan.Zero, first.Headers.CacheControl.MaxAge);
            previousTag = first.Headers.ETag!.ToString();
            using var conditional = new HttpRequestMessage(HttpMethod.Get, path);
            conditional.Headers.TryAddWithoutValidation("If-None-Match", previousTag);
            var unchanged = await client.SendAsync(conditional);
            Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
            Assert.True(unchanged.Headers.CacheControl!.NoCache);
        }
        File.WriteAllText(Path.Combine(webRoot, "index.html"), "<html>release two with new asset links</html>");
        File.SetLastWriteTimeUtc(Path.Combine(webRoot, "index.html"), new DateTime(2021, 1, 1));
        using var deployed = App(); using var browser = deployed.CreateClient();
        using var refresh = new HttpRequestMessage(HttpMethod.Get, path);
        refresh.Headers.TryAddWithoutValidation("If-None-Match", previousTag);
        var updated = await browser.SendAsync(refresh);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Contains("release two", await updated.Content.ReadAsStringAsync());
        Assert.True(updated.Headers.CacheControl!.NoCache);
        Assert.NotEqual(previousTag, updated.Headers.ETag!.ToString());
    }

    [Theory]
    [InlineData("/assets/index-a1b2c3d4.js", true)]
    [InlineData("/assets/index-e5f6g7h8.css", true)]
    [InlineData("/assets/config.js", false)]
    public async Task OnlyContentVersionedAssetsAreCachedLongTerm(string path, bool versioned)
    {
        using var app = App(); using var client = app.CreateClient();
        var response = await client.GetAsync(path); response.EnsureSuccessStatusCode();
        var cache = response.Headers.CacheControl!;
        Assert.Equal(versioned, cache.Public);
        Assert.Equal(!versioned, cache.NoCache);
        Assert.Equal(versioned ? TimeSpan.FromDays(365) : TimeSpan.Zero, cache.MaxAge);
        Assert.Equal(versioned, cache.Extensions.Any(extension => extension.Name == "immutable"));
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(directory, true);
    }
}
