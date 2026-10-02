using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dod.Api.Entries;
using Dod.Api.Social;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Dod.Api.Tests;

public sealed class SocialTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "dod-social-" + Guid.NewGuid());
    private readonly TestClock clock = new();
    private WebApplicationFactory<Program> App() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        { ["Storage:Path"] = Path.Combine(directory, "test.db"), ["Tracker:Password"] = "old-owner-password" }));
        builder.ConfigureServices(s => { s.RemoveAll<TimeProvider>(); s.AddSingleton<TimeProvider>(clock); });
    });
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static async Task Csrf(HttpClient client)
    {
        var json = await client.GetFromJsonAsync<JsonElement>("/api/account/csrf");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN"); client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", json.GetProperty("token").GetString());
    }
    private static async Task<HttpClient> User(WebApplicationFactory<Program> app, string nickname)
    {
        var client = app.CreateClient(); await Csrf(client);
        (await client.PostAsJsonAsync("/api/account/register", new { nickname, password = "a-long-test-password" })).EnsureSuccessStatusCode();
        await Csrf(client); return client;
    }
    private static async Task<string> Id(HttpClient client) => (await client.GetFromJsonAsync<JsonElement>("/api/account/me")).GetProperty("id").GetString()!;
    private static async Task Befriend(HttpClient a, HttpClient b, string nick)
    {
        (await a.PostAsJsonAsync("/api/social/friends", new { nickname = nick })).EnsureSuccessStatusCode();
        (await b.PutAsJsonAsync($"/api/social/friends/{await Id(a)}", new { accept = true })).EnsureSuccessStatusCode();
    }
    private static async Task<string> Challenge(HttpClient owner, string start = "2026-10-02")
    {
        var response = await owner.PostAsJsonAsync("/api/social/challenges", new { name = "October challenge", startDate = start, weeks = 1 });
        response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }
    [Fact]
    public async Task AccountsArePrivateUniqueAndRequireCsrf()
    {
        using var app = App(); using var a = await User(app, "Alice"); using var b = await User(app, "Bobby"); using var anon = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/entries/")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsJsonAsync("/api/account/register", new { nickname = "NoToken", password = "long-password-123" })).StatusCode);
        await Csrf(anon);
        Assert.Equal(HttpStatusCode.Conflict, (await anon.PostAsJsonAsync("/api/account/register", new { nickname = "ALICE", password = "long-password-123" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await b.PutAsJsonAsync("/api/account/profile", new { nickname = "alice" })).StatusCode);
        (await a.PutAsJsonAsync("/api/entries/2026-10-02/weight", new { weightKg = 90 })).EnsureSuccessStatusCode();
        (await a.PutAsJsonAsync("/api/settings/calorie-reference", new { calories = 2100 })).EnsureSuccessStatusCode();
        Assert.Empty((await b.GetFromJsonAsync<List<DailyEntry>>("/api/entries/"))!);
        Assert.Equal(JsonValueKind.Null, (await b.GetFromJsonAsync<JsonElement>("/api/settings/calorie-reference")).GetProperty("calories").ValueKind);
        (await b.PutAsJsonAsync("/api/entries/2026-10-02/weight", new { weightKg = 70 })).EnsureSuccessStatusCode();
        Assert.Equal(90, Assert.Single((await a.GetFromJsonAsync<List<DailyEntry>>("/api/entries/"))!).WeightKg);
        await a.PostAsync("/api/account/logout", null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await a.GetAsync("/api/entries/")).StatusCode);
    }
    [Fact]
    public async Task OwnerClaimRequiresOriginalSecretAndCanOnlyHappenOnce()
    {
        using var app = App(); using var c = app.CreateClient(); await Csrf(c);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/account/claim", new { nickname = "Owner", password = "new-password-123", ownerPassword = "wrong" })).StatusCode);
        (await c.PostAsJsonAsync("/api/account/claim", new { nickname = "Owner", password = "new-password-123", ownerPassword = "old-owner-password" })).EnsureSuccessStatusCode();
        Assert.Equal("legacy", await Id(c)); await Csrf(c);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/account/claim", new { nickname = "Hijack", password = "new-password-123", ownerPassword = "old-owner-password" })).StatusCode);
    }
    [Fact]
    public async Task FriendshipAcceptanceControlsInvitesAndPrivateLeaderboards()
    {
        using var app = App(); using var a = await User(app, "Alice"); using var b = await User(app, "Bobby"); using var c = await User(app, "Carol");
        var aid = await Id(a); var bid = await Id(b); var cid = await Id(c); var challenge = await Challenge(a);
        Assert.Equal(HttpStatusCode.NotFound, (await a.GetAsync("/api/social/users?nickname=Bob")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/api/social/users?nickname=bobby")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.PostAsJsonAsync($"/api/social/challenges/{challenge}/invite", new { userId = bid })).StatusCode);
        (await a.PostAsJsonAsync("/api/social/friends", new { nickname = "Bobby" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await b.PostAsJsonAsync("/api/social/friends", new { nickname = "Alice" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.PutAsJsonAsync($"/api/social/friends/{bid}", new { accept = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PutAsJsonAsync($"/api/social/friends/{aid}", new { accept = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.PostAsJsonAsync($"/api/social/challenges/{challenge}/invite", new { userId = bid })).StatusCode);
        (await b.PutAsJsonAsync($"/api/social/friends/{aid}", new { accept = true })).EnsureSuccessStatusCode();
        (await a.PostAsJsonAsync($"/api/social/challenges/{challenge}/invite", new { userId = bid })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/social/challenges/{challenge}/leaderboard")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PutAsJsonAsync($"/api/social/challenges/{challenge}/invitation", new { accept = true })).StatusCode);
        (await b.PutAsJsonAsync($"/api/social/challenges/{challenge}/invitation", new { accept = true })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await b.GetAsync($"/api/social/challenges/{challenge}/leaderboard")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/social/challenges/{challenge}/leaderboard")).StatusCode);
        // Accepted participants may invite their own friends, not the creator's friends.
        await Befriend(b, c, "Carol");
        Assert.Equal(HttpStatusCode.Forbidden, (await a.PostAsJsonAsync($"/api/social/challenges/{challenge}/invite", new { userId = cid })).StatusCode);
        (await b.PostAsJsonAsync($"/api/social/challenges/{challenge}/invite", new { userId = cid })).EnsureSuccessStatusCode();
        await b.DeleteAsync($"/api/social/friends/{cid}");
        Assert.Equal(HttpStatusCode.Conflict, (await c.PutAsJsonAsync($"/api/social/challenges/{challenge}/invitation", new { accept = true })).StatusCode);
        await b.DeleteAsync($"/api/social/challenges/{challenge}/participation");
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/social/challenges/{challenge}/leaderboard")).StatusCode);
    }
    [Fact]
    public async Task LeaderboardUsesChallengeWindowTiesMissingAndCorrections()
    {
        using var app = App(); using var a = await User(app, "Alice"); using var b = await User(app, "Bobby"); using var c = await User(app, "Carol");
        await Befriend(a, b, "Bobby"); await Befriend(a, c, "Carol"); var id = await Challenge(a);
        foreach (var client in new[] { b, c }) { (await a.PostAsJsonAsync($"/api/social/challenges/{id}/invite", new { userId = await Id(client) })).EnsureSuccessStatusCode(); (await client.PutAsJsonAsync($"/api/social/challenges/{id}/invitation", new { accept = true })).EnsureSuccessStatusCode(); }
        await a.PutAsJsonAsync("/api/entries/2026-10-01/weight", new { weightKg = 200 });
        await a.PutAsJsonAsync("/api/entries/2026-10-02/weight", new { weightKg = 90 });
        await a.PutAsJsonAsync("/api/entries/2026-10-08/weight", new { weightKg = 88 });
        await a.PutAsJsonAsync("/api/entries/2026-10-09/weight", new { weightKg = 60 });
        await b.PutAsJsonAsync("/api/entries/2026-10-03/weight", new { weightKg = 80 });
        await b.PutAsJsonAsync("/api/entries/2026-10-07/weight", new { weightKg = 78 });
        var rows = (await a.GetFromJsonAsync<List<Standing>>($"/api/social/challenges/{id}/leaderboard"))!;
        Assert.All(rows, r => Assert.Null(r.Rank)); // Future weights don't count.
        clock.Now = clock.Now.AddDays(8);
        // A challenge can outlive the seven-day session; sign in again after advancing time.
        foreach (var pair in new[] { (Client: a, Nick: "Alice"), (Client: b, Nick: "Bobby"), (Client: c, Nick: "Carol") })
        {
            await Csrf(pair.Client);
            (await pair.Client.PostAsJsonAsync("/api/account/login", new { nickname = pair.Nick, password = "a-long-test-password" })).EnsureSuccessStatusCode();
            await Csrf(pair.Client);
        }
        rows = (await a.GetFromJsonAsync<List<Standing>>($"/api/social/challenges/{id}/leaderboard"))!;
        Assert.Equal(new decimal?[] { 2, 2, null }, rows.Select(r => r.KgLost)); Assert.Equal(new int?[] { 1, 1, null }, rows.Select(r => r.Rank));
        Assert.Equal("2026-10-02", rows[0].BaselineDate); Assert.Equal("2026-10-08", rows[0].LatestDate);
        await b.PutAsJsonAsync("/api/entries/2026-10-07/weight", new { weightKg = 81 });
        rows = (await a.GetFromJsonAsync<List<Standing>>($"/api/social/challenges/{id}/leaderboard"))!;
        Assert.Equal(-1, rows[1].KgLost); Assert.Equal(2, rows[1].Rank);
        var second = await Challenge(a, "2026-10-11");
        (await a.PostAsJsonAsync($"/api/social/challenges/{second}/invite", new { userId = await Id(b) })).EnsureSuccessStatusCode();
        clock.Now = clock.Now.AddDays(3);
        Assert.Equal(HttpStatusCode.Conflict, (await b.PutAsJsonAsync($"/api/social/challenges/{second}/invitation", new { accept = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.PostAsJsonAsync($"/api/social/challenges/{id}/invite", new { userId = await Id(c) })).StatusCode);
    }
    [Fact]
    public async Task AvatarUploadsAreBoundedAndPersistAcrossLogin()
    {
        using var app = App(); using var a = await User(app, "Alice"); using var b = await User(app, "Bobby"); var id = await Id(a);
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aDc8AAAAASUVORK5CYII=");
        using var content = new ByteArrayContent(png); content.Headers.ContentType = new("image/png");
        (await a.PutAsync("/api/account/avatar", content)).EnsureSuccessStatusCode();
        Assert.Equal(png, await b.GetByteArrayAsync($"/api/avatars/{id}"));
        using var bad = new StringContent("<svg></svg>"); bad.Headers.ContentType = new("image/svg+xml");
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PutAsync("/api/account/avatar", bad)).StatusCode);
        using var huge = new ByteArrayContent(new byte[1024 * 1024 + 1]); huge.Headers.ContentType = new("image/png");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await a.PutAsync("/api/account/avatar", huge)).StatusCode);
        await a.PostAsync("/api/account/logout", null); await Csrf(a);
        (await a.PostAsJsonAsync("/api/account/login", new { nickname = "ALICE", password = "a-long-test-password" })).EnsureSuccessStatusCode(); await Csrf(a);
        Assert.Equal(png, await a.GetByteArrayAsync($"/api/avatars/{id}"));
        await a.DeleteAsync("/api/account/avatar"); Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/avatars/{id}")).StatusCode);
    }
    [Fact]
    public async Task ChallengeChartSharesOnlyAcceptedParticipantsAndLatestDailyWeightInWindow()
    {
        using var app = App();
        using var owner = await User(app, "Owner");
        using var friend = await User(app, "Friend");
        using var pending = await User(app, "Pending");
        using var outsider = await User(app, "Outsider");
        using var anonymous = app.CreateClient();
        await Befriend(owner, friend, "Friend");
        await Befriend(owner, pending, "Pending");
        var id = await Challenge(owner);
        var ownerId = await Id(owner);
        var friendId = await Id(friend);
        foreach (var person in new[] { friend, pending })
            (await owner.PostAsJsonAsync($"/api/social/challenges/{id}/invite", new { userId = await Id(person) })).EnsureSuccessStatusCode();
        (await friend.PutAsJsonAsync($"/api/social/challenges/{id}/invitation", new { accept = true })).EnsureSuccessStatusCode();
        foreach (var row in new[] { (Date: "2026-10-01", Weight: 99m), (Date: "2026-10-02", Weight: 90m),
            (Date: "2026-10-02", Weight: 89.5m), (Date: "2026-10-04", Weight: 88m),
            (Date: "2026-10-08", Weight: 87m), (Date: "2026-10-09", Weight: 86m) })
            (await owner.PutAsJsonAsync($"/api/entries/{row.Date}/weight", new { weightKg = row.Weight })).EnsureSuccessStatusCode();
        await pending.PutAsJsonAsync("/api/entries/2026-10-02/weight", new { weightKg = 75 });
        var route = $"/api/social/challenges/{id}/weights";
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await pending.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync(route)).StatusCode);
        var rows = (await friend.GetFromJsonAsync<List<ParticipantWeights>>(route))!;
        Assert.Equal(2, rows.Count);
        Assert.Empty(rows.Single(p => p.Id == friendId).Weights);
        var first = Assert.Single(rows.Single(p => p.Id == ownerId).Weights);
        Assert.Equal("2026-10-02", first.Date);
        Assert.Equal(89.5m, first.WeightKg);
        // Six days later, the last included challenge day is visible. End-date entries remain excluded.
        clock.Now = clock.Now.AddDays(6);
        rows = (await friend.GetFromJsonAsync<List<ParticipantWeights>>(route))!;
        Assert.Equal(new[] { "2026-10-02", "2026-10-04", "2026-10-08" }, rows.Single(p => p.Id == ownerId).Weights.Select(w => w.Date));
        await owner.PutAsJsonAsync("/api/entries/2026-10-04/weight", new { weightKg = 87.75m });
        rows = (await friend.GetFromJsonAsync<List<ParticipantWeights>>(route))!;
        Assert.Equal(87.75m, rows.Single(p => p.Id == ownerId).Weights[1].WeightKg);
        await friend.DeleteAsync($"/api/social/challenges/{id}/participation");
        Assert.Equal(HttpStatusCode.NotFound, (await friend.GetAsync(route)).StatusCode);
        Assert.Single((await owner.GetFromJsonAsync<List<ParticipantWeights>>(route))!);
    }

    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
