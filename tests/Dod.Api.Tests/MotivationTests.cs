using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dod.Api.Entries;
using Dod.Api.Motivation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Dod.Api.Tests;

public sealed class MotivationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "dod-xp-" + Guid.NewGuid());
    private readonly TestClock clock = new();
    private string Today => clock.GetUtcNow().ToString("yyyy-MM-dd");
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private WebApplicationFactory<Program> App() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        { ["Storage:Path"] = Path.Combine(directory, "test.db"), ["Tracker:Password"] = "original-secret" }));
        builder.ConfigureServices(services => { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock); });
    });
    private static async Task Csrf(HttpClient client)
    {
        var json = await client.GetFromJsonAsync<JsonElement>("/api/account/csrf");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", json.GetProperty("token").GetString());
    }
    private static async Task<HttpClient> Login(WebApplicationFactory<Program> app, string name, bool admin = false, bool existing = false)
    {
        var client = app.CreateClient();
        await Csrf(client);
        var action = existing ? "login" : admin ? "claim" : "register";
        (await client.PostAsJsonAsync($"/api/account/{action}", new { nickname = name, password = "test-password-123", ownerPassword = "original-secret" })).EnsureSuccessStatusCode();
        await Csrf(client);
        return client;
    }
    private static async Task<long> Total(HttpClient client) => (await client.GetFromJsonAsync<Experience>("/api/motivation/experience"))!.TotalXp;
    private async Task<HttpResponseMessage> Report(HttpClient client, string id, bool completed, string? date = null) =>
        await client.PutAsJsonAsync($"/api/motivation/{date ?? Today}/activities/{id}", new { completed });

    [Fact]
    public async Task WeightAutomaticallyAwardsOncePerDayEvenWithConcurrentUpdates()
    {
        using var app = App(); using var user = await Login(app, "Alice"); using var other = await Login(app, "Bobby");
        await user.PutAsJsonAsync($"/api/entries/{Today}/calories", new { caloriesBurned = 2000 });
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => user.PutAsJsonAsync($"/api/entries/{Today}/weight", new { weightKg = 80 + i })));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
        Assert.Equal(10, responses.Sum(r => long.Parse(r.Headers.GetValues("X-XP-Change").Single())));
        Assert.Equal(10, await Total(user)); Assert.Equal(0, await Total(other));
        var entry = Assert.Single((await user.GetFromJsonAsync<List<DailyEntry>>("/api/entries/"))!);
        Assert.Equal(2000, entry.CaloriesBurned);
        Assert.Equal(HttpStatusCode.BadRequest, (await Report(user, "weight", true)).StatusCode);
        var day = (await user.GetFromJsonAsync<MotivationDay>($"/api/motivation/{Today}"))!;
        Assert.Equal(10, day.Activities.Single(a => a.Id == "weight").Earned);
        Assert.False(day.Activities.Single(a => a.Id == "weight").CanReport);
    }

    [Fact]
    public async Task ManualReportsCanBeCorrectedWithoutFarmingAndPersistAcrossRestart()
    {
        using (var app = App())
        using (var user = await Login(app, "Alice"))
        {
            Assert.Null((await user.GetFromJsonAsync<MotivationDay>($"/api/motivation/{Today}"))!.Activities.Single(a => a.Id == "no-sweets").Completed);
            var first = await Report(user, "no-sweets", true); first.EnsureSuccessStatusCode();
            Assert.Equal(10, (await first.Content.ReadFromJsonAsync<XpChange>())!.Awarded);
            Assert.Equal(0, (await (await Report(user, "no-sweets", true)).Content.ReadFromJsonAsync<XpChange>())!.Awarded);
            Assert.Equal(-10, (await (await Report(user, "no-sweets", false)).Content.ReadFromJsonAsync<XpChange>())!.Awarded);
            Assert.Equal(0, await Total(user));
            (await Report(user, "no-sweets", true)).EnsureSuccessStatusCode();
            Assert.Equal(10, await Total(user));
            var missing = await user.PutAsJsonAsync($"/api/motivation/{Today}/activities/no-sweets", new { points = 1000 });
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            Assert.Equal(10, await Total(user));
        }
        using var restarted = App(); using var signed = await Login(restarted, "Alice", existing: true);
        Assert.Equal(10, await Total(signed));
    }

    [Fact]
    public async Task AdminOnlyCanManageActivitiesAndViewUsersAndChallenges()
    {
        using var app = App(); using var owner = await Login(app, "Owner", admin: true); using var user = await Login(app, "Alice"); using var anon = app.CreateClient();
        Assert.True((await owner.GetFromJsonAsync<JsonElement>("/api/account/me")).GetProperty("isAdmin").GetBoolean());
        Assert.False((await user.GetFromJsonAsync<JsonElement>("/api/account/me")).GetProperty("isAdmin").GetBoolean());
        foreach (var path in new[] { "users", "challenges", "activities" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"/api/admin/{path}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync($"/api/admin/{path}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/admin/{path}")).StatusCode);
        }
        var input = new ActivityInput("Daily walk", "Did you take a walk?", 100, null, true);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsJsonAsync("/api/admin/activities", input)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PutAsJsonAsync("/api/admin/activities/no-sweets", input)).StatusCode);
        (await owner.PostAsJsonAsync("/api/admin/activities", input)).EnsureSuccessStatusCode();
        var id = (await owner.GetFromJsonAsync<List<ActivityDefinition>>("/api/admin/activities"))!.Single(a => a.Name == "Daily walk").Id;
        var result = (await (await Report(user, id, true)).Content.ReadFromJsonAsync<XpChange>())!;
        Assert.True(result.LeveledUp); Assert.Equal(2, result.Experience.Level); Assert.Equal(100, result.Experience.TotalXp); Assert.Equal(100, result.Experience.XpToNextLevel);
        await user.PostAsJsonAsync("/api/social/challenges", new { name = "A challenge", startDate = Today, weeks = 2 });
        Assert.Single((await owner.GetFromJsonAsync<List<AdminChallenge>>("/api/admin/challenges"))!);
        var users = (await owner.GetFromJsonAsync<List<AdminUser>>("/api/admin/users"))!;
        Assert.Equal(2, users.Count); Assert.Equal(100, users.Single(u => u.Nickname == "Alice").Experience.TotalXp);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/admin/activities", input with { Points = -5 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/admin/activities", input with { CutoffTime = "25:00" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/admin/activities", input with { Points = 1001 })).StatusCode);
    }

    [Fact]
    public async Task RuleEditsPreserveReportedRewardsAndRetirementPreservesHistory()
    {
        using var app = App(); using var owner = await Login(app, "Owner", admin: true); using var user = await Login(app, "Alice"); using var other = await Login(app, "Bobby");
        (await Report(user, "no-late-food", true)).EnsureSuccessStatusCode();
        var edited = new ActivityInput("Earlier cutoff", "Did you meet the new goal?", 25, "19:00", true);
        (await owner.PutAsJsonAsync("/api/admin/activities/no-late-food", edited)).EnsureSuccessStatusCode();
        var previous = (await user.GetFromJsonAsync<MotivationDay>($"/api/motivation/{Today}"))!.Activities.Single(a => a.Id == "no-late-food");
        Assert.Equal(10, previous.Points); Assert.Equal("20:00", previous.CutoffTime); Assert.Equal("No food after cutoff", previous.Name);
        await Report(user, "no-late-food", false); await Report(user, "no-late-food", true);
        Assert.Equal(10, await Total(user));
        clock.Now = clock.Now.AddDays(1);
        (await Report(user, "no-late-food", true)).EnsureSuccessStatusCode(); Assert.Equal(35, await Total(user));
        (await owner.PutAsJsonAsync("/api/admin/activities/no-late-food", edited with { IsActive = false })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await Report(other, "no-late-food", true)).StatusCode);
        (await Report(user, "no-late-food", false)).EnsureSuccessStatusCode(); Assert.Equal(10, await Total(user));
        Assert.DoesNotContain((await other.GetFromJsonAsync<MotivationDay>($"/api/motivation/{Today}"))!.Activities, a => a.Id == "no-late-food");
    }

    [Fact]
    public async Task FutureReportsCannotEarnXpAndWeightRuleChangesDoNotReawardSameDate()
    {
        using var app = App(); using var owner = await Login(app, "Owner", admin: true); using var user = await Login(app, "Alice");
        var tomorrow = clock.Now.AddDays(1).ToString("yyyy-MM-dd");
        Assert.Equal(HttpStatusCode.BadRequest, (await Report(user, "no-sweets", true, tomorrow)).StatusCode);
        await user.PutAsJsonAsync($"/api/entries/{tomorrow}/weight", new { weightKg = 80 });
        Assert.Equal(0, await Total(user));
        await user.PutAsJsonAsync($"/api/entries/{Today}/weight", new { weightKg = 81 });
        await owner.PutAsJsonAsync("/api/admin/activities/weight", new ActivityInput("Weigh yourself", "Record weight", 30, null, true));
        await user.PutAsJsonAsync($"/api/entries/{Today}/weight", new { weightKg = 80.5 }); Assert.Equal(10, await Total(user));
        clock.Now = clock.Now.AddDays(1);
        await user.PutAsJsonAsync($"/api/entries/{Today}/weight", new { weightKg = 80 }); Assert.Equal(40, await Total(user));
    }

    [Fact]
    public async Task UpgradeFromVersionFourPreservesDataAndDoesNotRetroactivelyAwardXp()
    {
        Directory.CreateDirectory(directory);
        await using (var db = new SqliteConnection($"Data Source={Path.Combine(directory, "test.db")}"))
        {
            await db.OpenAsync(); using var cmd = db.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE Users (Id TEXT PRIMARY KEY,Nickname TEXT NOT NULL,NormalizedNickname TEXT UNIQUE,PasswordHash TEXT NULL,Avatar BLOB NULL,AvatarType TEXT NULL);
                INSERT INTO Users (Id,Nickname,NormalizedNickname) VALUES ('legacy','Reserved owner','!OWNER');
                CREATE TABLE Entries (UserId TEXT NOT NULL,Date TEXT NOT NULL,WeightKg REAL NULL,CaloriesBurned INTEGER NULL,CaloriesEaten INTEGER NULL,PRIMARY KEY(UserId,Date));
                INSERT INTO Entries VALUES ('legacy','2026-09-01',90,2000,2100);
                CREATE TABLE CalorieReference (UserId TEXT PRIMARY KEY,Calories INTEGER NOT NULL);
                INSERT INTO CalorieReference VALUES ('legacy',2200);
                PRAGMA user_version=4;
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        using var app = App(); using var owner = await Login(app, "Owner", admin: true);
        Assert.Equal(0, await Total(owner));
        var entry = Assert.Single((await owner.GetFromJsonAsync<List<DailyEntry>>("/api/entries/"))!);
        Assert.Equal(90, entry.WeightKg); Assert.Equal(2100, entry.CaloriesEaten);
        Assert.True((await owner.GetFromJsonAsync<JsonElement>("/api/account/me")).GetProperty("isAdmin").GetBoolean());
        await owner.PutAsJsonAsync("/api/entries/2026-09-01/weight", new { weightKg = 89 });
        Assert.Equal(0, await Total(owner));
        var day = (await owner.GetFromJsonAsync<MotivationDay>("/api/motivation/2026-09-01"))!;
        Assert.Equal(0, day.Activities.Single(a => a.Id == "weight").Earned);
    }

    [Theory]
    [InlineData(0,1,0,100)]
    [InlineData(99,1,99,1)]
    [InlineData(100,2,0,100)]
    [InlineData(250,3,50,50)]
    public void ExperienceLevelsHaveStableBoundaries(long total, long level, long into, long remaining)
    {
        var result = Experience.From(total);
        Assert.Equal(level, result.Level); Assert.Equal(into, result.XpIntoLevel); Assert.Equal(remaining, result.XpToNextLevel);
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
