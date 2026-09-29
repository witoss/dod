using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using Dod.Api.Entries;
using Dod.Api.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Dod.Api.Tests;

public sealed class EntryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "dod-tests-" + Guid.NewGuid());
    private WebApplicationFactory<Program> CreateApp(string? password = null) => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:Path"] = Path.Combine(directory, "test.db"),
                ["Tracker:Password"] = password
            }));
        });

    [Fact]
    public async Task IndependentUpdatesPreserveOtherFieldAndSurviveRestart()
    {
        using (var app = CreateApp())
        using (var client = app.CreateClient())
        {
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/entries/2026-09-28/weight", new { weightKg = 75.25 })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/entries/2026-09-28/calories", new { caloriesBurned = 2400 })).StatusCode);
            await client.PutAsJsonAsync("/api/entries/2026-09-28/weight", new { weightKg = 74.5 });
        }
        using var restarted = CreateApp();
        using var restartedClient = restarted.CreateClient();
        var entry = Assert.Single((await restartedClient.GetFromJsonAsync<List<DailyEntry>>("/api/entries/"))!);
        Assert.Equal(74.5m, entry.WeightKg);
        Assert.Equal(2400, entry.CaloriesBurned);
    }

    [Theory]
    [InlineData("weight", "{\"weightKg\":0}")]
    [InlineData("weight", "{\"weightKg\":1001}")]
    [InlineData("calories", "{\"caloriesBurned\":-1}")]
    [InlineData("calories", "{\"caloriesBurned\":1.5}")]
    public async Task InvalidInputIsRejectedWithoutWriting(string field, string json)
    {
        using var app = CreateApp(); using var client = app.CreateClient();
        var response = await client.PutAsync($"/api/entries/2026-09-28/{field}", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<DailyEntry>>("/api/entries/"))!);
    }

    [Fact]
    public async Task ZeroCaloriesIsAnEntryAndWeightRemainsMissing()
    {
        using var app = CreateApp(); using var client = app.CreateClient();
        await client.PutAsJsonAsync("/api/entries/2026-09-28/calories", new { caloriesBurned = 0 });
        var entry = Assert.Single((await client.GetFromJsonAsync<List<DailyEntry>>("/api/entries/"))!);
        Assert.Null(entry.WeightKg); Assert.Equal(0, entry.CaloriesBurned);
    }

    [Fact]
    public async Task PasswordProtectsDataButAllowsHealthProbe()
    {
        using var app = CreateApp("test-password"); using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/entries/")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("tracker:wrong")));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/entries/")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("tracker:test-password")));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/entries/")).StatusCode);
    }
    [Fact]
    public async Task ReferenceStartsUnsetAndUpdatesSurviveRestart()
    {
        using (var app = CreateApp())
        using (var client = app.CreateClient())
        {
            Assert.Null((await client.GetFromJsonAsync<CalorieReferenceResponse>("/api/settings/calorie-reference"))!.Calories);
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/settings/calorie-reference", new { calories = 2000 })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/settings/calorie-reference", new { calories = 2200 })).StatusCode);
        }
        using var restarted = CreateApp();
        using var restartedClient = restarted.CreateClient();
        Assert.Equal(2200, (await restartedClient.GetFromJsonAsync<CalorieReferenceResponse>("/api/settings/calorie-reference"))!.Calories);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"calories\":null}")]
    [InlineData("{\"calories\":0}")]
    [InlineData("{\"calories\":-1}")]
    [InlineData("{\"calories\":100001}")]
    [InlineData("{\"calories\":2200.5}")]
    public async Task InvalidReferenceDoesNotOverwriteSavedValue(string json)
    {
        using var app = CreateApp();
        using var client = app.CreateClient();
        await client.PutAsJsonAsync("/api/settings/calorie-reference", new { calories = 2200 });
        var response = await client.PutAsync("/api/settings/calorie-reference", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(2200, (await client.GetFromJsonAsync<CalorieReferenceResponse>("/api/settings/calorie-reference"))!.Calories);
    }

    [Fact]
    public async Task ReferenceRequiresPasswordForReadsAndWrites()
    {
        using var app = CreateApp("test-password");
        using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/settings/calorie-reference")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync("/api/settings/calorie-reference", new { calories = 2200 })).StatusCode);
    }

    [Fact]
    public async Task UpgradesOriginalDatabaseWithoutLosingMeasurements()
    {
        Directory.CreateDirectory(directory);
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "test.db")}"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE Entries (Date TEXT PRIMARY KEY, WeightKg REAL NULL, CaloriesBurned INTEGER NULL);
                INSERT INTO Entries VALUES ('2026-09-28', 75.25, 2400);
                """;
            await command.ExecuteNonQueryAsync();
        }
        using var app = CreateApp();
        using var client = app.CreateClient();
        var entry = Assert.Single((await client.GetFromJsonAsync<List<DailyEntry>>("/api/entries/"))!);
        Assert.Equal(75.25m, entry.WeightKg);
        Assert.Equal(2400, entry.CaloriesBurned);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/settings/calorie-reference", new { calories = 2200 })).StatusCode);
        Assert.Equal(2200, (await client.GetFromJsonAsync<CalorieReferenceResponse>("/api/settings/calorie-reference"))!.Calories);
    }

    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
