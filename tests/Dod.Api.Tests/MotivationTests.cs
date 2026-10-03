using Dod.Api.Motivation.Persistence;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dod.Api.Entries;
using Dod.Api.Motivation;
using Dod.Api.Notifications;
using Dod.Api.Motivation.Domain;
using Dod.Api.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Dod.Api.Tests;

public sealed class MotivationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "dod-xp-" + Guid.NewGuid());
    private readonly TestClock clock = new();
    private readonly ProgressLogs progressLogs = new();
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
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<ILoggerProvider>(progressLogs);
            var worker = services.Single(s => s.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
                && s.ImplementationType == typeof(EmailWorker));
            services.Remove(worker);
        });
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

    private sealed class ProgressLogs : ILoggerProvider
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Messages = new();
        public ILogger CreateLogger(string category) => new ProgressLogger(category, Messages);
        public void Dispose() { }
        private sealed class ProgressLogger(string category, System.Collections.Concurrent.ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
            {
                if (category == typeof(MotivationStore).FullName || category == typeof(ApiRequestLoggingMiddleware).FullName)
                    messages.Enqueue(formatter(state, error));
            }
        }
    }
    // Keep test cookies valid against the test client's real clock while moving the domain clock.
    private static readonly DateOnly HistoricalWeek = WeeklyRules.NextMonday(DateOnly.FromDateTime(DateTime.UtcNow));
    private static DateTimeOffset At(DateOnly date) => new(date.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
    private async Task CompleteWeek(HttpClient user, DateOnly monday, string? missingActivity = null)
    {
        for (var day = monday; day < monday.AddDays(7); day = day.AddDays(1))
        {
            (await user.PutAsJsonAsync($"/api/entries/{day:yyyy-MM-dd}/weight", new { weightKg = 80 })).EnsureSuccessStatusCode();
            foreach (var id in new[] { "no-sweets", "no-late-food" })
                if (id != missingActivity || day != monday.AddDays(4))
                    (await Report(user, id, true, WeeklyRules.Day(day))).EnsureSuccessStatusCode();
        }
    }
    private static async Task<WeeklySummary> Week(HttpClient user, DateOnly monday) =>
        (await user.GetFromJsonAsync<WeeklySummary>($"/api/motivation/weekly/{monday:yyyy-MM-dd}"))!;

    [Fact]
    public async Task LateCorrectionsAddRemoveRestoreOnlyAffectedWeeksBonusAndSurviveRestart()
    {
        clock.Now = At(HistoricalWeek.AddDays(-7));
        using (var app = App())
        {
            using var initial = await Login(app, "Alice");
            clock.Now = At(HistoricalWeek.AddDays(14));
            using var user = await Login(app, "Alice", existing: true); using var other = await Login(app, "Bobby");
            await CompleteWeek(user, HistoricalWeek, "no-late-food");
            await CompleteWeek(user, HistoricalWeek.AddDays(7));
            Assert.Equal(460, await Total(user));
            Assert.Equal(0, (await Week(user, HistoricalWeek)).BonusXp);
            Assert.Equal(50, (await Week(user, HistoricalWeek.AddDays(7))).BonusXp);
            var missingDate = WeeklyRules.Day(HistoricalWeek.AddDays(4));
            var day = (await user.GetFromJsonAsync<MotivationDay>($"/api/motivation/{missingDate}"))!;
            Assert.True(day.Activities.Single(a => a.Id == "no-late-food").CanReport);
            Assert.Null(day.Activities.Single(a => a.Id == "no-late-food").Completed);
            var addResponse = await Report(user, "no-late-food", true, missingDate); addResponse.EnsureSuccessStatusCode();
            var added = (await addResponse.Content.ReadFromJsonAsync<XpChange>())!;
            Assert.Equal(60, added.Awarded); Assert.Equal(10, added.ActivityChange); Assert.Equal(50, added.WeeklyBonusChange);
            Assert.Equal("50", addResponse.Headers.GetValues("X-XP-Weekly-Bonus-Change").Single());
            Assert.Equal(520, await Total(user));
            var repeated = (await (await Report(user, "no-late-food", true, missingDate)).Content.ReadFromJsonAsync<XpChange>())!;
            Assert.Equal(0, repeated.Awarded); Assert.Equal(0, repeated.WeeklyBonusChange);
            var removed = (await (await Report(user, "no-sweets", false, missingDate)).Content.ReadFromJsonAsync<XpChange>())!;
            Assert.Equal(-60, removed.Awarded); Assert.Equal(-10, removed.ActivityChange); Assert.Equal(-50, removed.WeeklyBonusChange);
            Assert.Equal(5, removed.Experience.Level); Assert.False(removed.LeveledUp);
            Assert.Equal(0, (await Week(user, HistoricalWeek)).BonusXp);
            Assert.Equal(50, (await Week(user, HistoricalWeek.AddDays(7))).BonusXp);
            var restore = (await (await Report(user, "no-sweets", true, missingDate)).Content.ReadFromJsonAsync<XpChange>())!;
            Assert.Equal(60, restore.Awarded); Assert.True(restore.LeveledUp);
            Assert.Equal(520, await Total(user)); Assert.Equal(0, await Total(other));
            Assert.Contains(progressLogs.Messages, message => message.Contains("weekly bonus change -50"));
        }
        using var restarted = App(); using var renewed = await Login(restarted, "Alice", existing: true);
        Assert.Equal(520, await Total(renewed));
        Assert.Equal(50, (await Week(renewed, HistoricalWeek)).BonusXp);
        Assert.Equal(50, (await Week(renewed, HistoricalWeek.AddDays(7))).BonusXp);
    }

    [Fact]
    public async Task ConcurrentLateTogglesAwardAndRemoveDailyXpAndBonusExactlyOnce()
    {
        clock.Now = At(HistoricalWeek.AddDays(-7));
        using var app = App(); using var initial = await Login(app, "Alice");
        clock.Now = At(HistoricalWeek.AddDays(7)); using var user = await Login(app, "Alice", existing: true);
        await CompleteWeek(user, HistoricalWeek, "no-sweets");
        var date = WeeklyRules.Day(HistoricalWeek.AddDays(4));
        var additions = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Report(user, "no-sweets", true, date)));
        Assert.All(additions, r => r.EnsureSuccessStatusCode());
        Assert.Equal(60, additions.Sum(r => long.Parse(r.Headers.GetValues("X-XP-Change").Single())));
        Assert.Equal(50, additions.Sum(r => int.Parse(r.Headers.GetValues("X-XP-Weekly-Bonus-Change").Single())));
        Assert.Equal(260, await Total(user));
        var removals = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Report(user, "no-sweets", false, date)));
        Assert.All(removals, r => r.EnsureSuccessStatusCode());
        Assert.Equal(-60, removals.Sum(r => long.Parse(r.Headers.GetValues("X-XP-Change").Single())));
        Assert.Equal(-50, removals.Sum(r => int.Parse(r.Headers.GetValues("X-XP-Weekly-Bonus-Change").Single())));
        Assert.Equal(200, await Total(user)); Assert.Equal(0, (await Week(user, HistoricalWeek)).BonusXp);
    }

    [Fact]
    public async Task RetiredGoalsRemainEditableForTheirPastRosterAndKeepFirstReportReward()
    {
        clock.Now = At(HistoricalWeek.AddDays(-7));
        using var app = App(); using var initialOwner = await Login(app, "Owner", admin: true); using var initial = await Login(app, "Alice");
        clock.Now = At(HistoricalWeek.AddDays(7));
        using var owner = await Login(app, "Owner", existing: true); using var user = await Login(app, "Alice", existing: true);
        await CompleteWeek(user, HistoricalWeek, "no-late-food");
        var date = WeeklyRules.Day(HistoricalWeek.AddDays(4));
        (await Report(user, "no-late-food", false, date)).EnsureSuccessStatusCode();
        (await owner.PutAsJsonAsync("/api/admin/activities/no-late-food", new ActivityInput("Earlier cutoff", "Changed rule", 100, "18:00", false))).EnsureSuccessStatusCode();
        var past = (await user.GetFromJsonAsync<MotivationDay>($"/api/motivation/{date}"))!.Activities.Single(a => a.Id == "no-late-food");
        Assert.True(past.CanReport); Assert.False(past.IsActive); Assert.Equal(10, past.Points); Assert.Equal("20:00", past.CutoffTime);
        var change = (await (await Report(user, "no-late-food", true, date)).Content.ReadFromJsonAsync<XpChange>())!;
        Assert.Equal(60, change.Awarded); Assert.Equal(50, change.WeeklyBonusChange);
        var notIntroduced = HistoricalWeek.AddDays(-14).ToString("yyyy-MM-dd");
        Assert.Equal(HttpStatusCode.BadRequest, (await Report(user, "no-late-food", true, notIntroduced)).StatusCode);
        clock.Now = At(HistoricalWeek.AddDays(14));
        using var renewed = await Login(app, "Alice", existing: true);
        Assert.Equal(HttpStatusCode.BadRequest, (await Report(renewed, "no-late-food", true)).StatusCode);
        Assert.Equal(260, await Total(renewed));
    }

    [Fact]
    public async Task LastWeightBackfillCompletesClosedWeekButPartialAndOnboardingWeeksEarnNoBonus()
    {
        clock.Now = At(HistoricalWeek.AddDays(-7));
        using var app = App(); using var initial = await Login(app, "Alice");
        clock.Now = At(HistoricalWeek.AddDays(7)); using var user = await Login(app, "Alice", existing: true);
        for (var day = HistoricalWeek; day < HistoricalWeek.AddDays(7); day = day.AddDays(1))
        {
            foreach (var id in new[] { "no-sweets", "no-late-food" }) (await Report(user, id, true, WeeklyRules.Day(day))).EnsureSuccessStatusCode();
            if (day != HistoricalWeek.AddDays(6)) (await user.PutAsJsonAsync($"/api/entries/{day:yyyy-MM-dd}/weight", new { weightKg = 80 })).EnsureSuccessStatusCode();
        }
        Assert.Equal(200, await Total(user));
        var response = await user.PutAsJsonAsync($"/api/entries/{HistoricalWeek.AddDays(6):yyyy-MM-dd}/weight", new { weightKg = 80 });
        response.EnsureSuccessStatusCode();
        Assert.Equal("60", response.Headers.GetValues("X-XP-Change").Single());
        Assert.Equal("50", response.Headers.GetValues("X-XP-Weekly-Bonus-Change").Single());
        await CompleteWeek(user, HistoricalWeek.AddDays(-7));
        Assert.Equal(0, (await Week(user, HistoricalWeek.AddDays(-7))).BonusXp);
        clock.Now = At(HistoricalWeek.AddDays(13)); using var renewed = await Login(app, "Alice", existing: true);
        await CompleteWeek(renewed, HistoricalWeek.AddDays(7));
        Assert.Equal(0, (await Week(renewed, HistoricalWeek.AddDays(7))).BonusXp);
        clock.Now = At(HistoricalWeek.AddDays(14));
        (await Report(renewed, "no-sweets", true, WeeklyRules.Day(HistoricalWeek.AddDays(7)))).EnsureSuccessStatusCode();
        Assert.Equal(50, (await Week(renewed, HistoricalWeek.AddDays(7))).BonusXp);
    }

    [Fact]
    public async Task MiddlewarePreservesCsrfAndLogsTraceAndRouteWithoutPrivatePayload()
    {
        using var app = App(); using var user = await Login(app, "Alice");
        user.DefaultRequestHeaders.Add("X-Request-ID", "untrusted-client-id");
        var response = await user.PutAsJsonAsync($"/api/motivation/{Today}/activities/no-sweets?private=secret-query",
            new { completed = true, privateValue = "secret-body" });
        response.EnsureSuccessStatusCode();
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var trace = response.Headers.GetValues("X-Request-ID").Single(); Assert.NotEqual("untrusted-client-id", trace);
        Assert.Contains(progressLogs.Messages, message => message.Contains(trace) && message.Contains("{date}/activities/{id}"));
        Assert.All(progressLogs.Messages, message =>
        { Assert.DoesNotContain("secret-query", message); Assert.DoesNotContain("secret-body", message); });
        user.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await Report(user, "no-sweets", false)).StatusCode);
        Assert.Equal(10, await Total(user));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(3, 20, 0)]
    [InlineData(3, 21, 50)]
    [InlineData(3, 22, 0)]
    public void WeeklyBonusRequiresEveryRosterGoalOnEveryDay(int goals, int reports, int expected) =>
        Assert.Equal(expected, GoalProgress.WeeklyBonus(goals, reports));

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
        (await Report(other, "no-late-food", true)).EnsureSuccessStatusCode();
        (await Report(user, "no-late-food", false)).EnsureSuccessStatusCode(); Assert.Equal(10, await Total(user));
        Assert.Contains((await other.GetFromJsonAsync<MotivationDay>($"/api/motivation/{Today}"))!.Activities, a => a.Id == "no-late-food");
        clock.Now = new DateTimeOffset(WeeklyRules.NextMonday(DateOnly.FromDateTime(clock.Now.UtcDateTime)).ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
        using var renewed = await Login(app, "Bobby", existing: true);
        Assert.Equal(HttpStatusCode.BadRequest, (await Report(renewed, "no-late-food", true)).StatusCode);
        Assert.DoesNotContain((await renewed.GetFromJsonAsync<MotivationDay>($"/api/motivation/{Today}"))!.Activities, a => a.Id == "no-late-food");
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
