using Dod.Api.Motivation.Persistence;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dod.Api.Entries;
using Dod.Api.Motivation;
using Dod.Api.Notifications;
using Dod.Api.Accounts;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace Dod.Api.Tests;

public sealed class EmailTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "dod-email-" + Guid.NewGuid());
    private readonly Clock clock = new();
    private readonly Transport transport = new();
    private readonly QueueLogs logs = new();
    private sealed class Clock : TimeProvider
    {
        public readonly DateOnly Week = WeeklyRules.NextMonday(DateOnly.FromDateTime(DateTime.UtcNow));
        public DateTimeOffset Now;
        public Clock() => Now = At(Week.AddDays(-1), 12);
        public static DateTimeOffset At(DateOnly date, int hour, int minute = 0) => new(date.ToDateTime(new TimeOnly(hour, minute)), TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Transport : IEmailTransport
    {
        public bool Available { get; set; } = true;
        public bool Fail;
        public Exception? Failure;
        public TaskCompletionSource? SendEntered;
        public TaskCompletionSource? SendRelease;
        public List<(EmailMessage Message, string Id)> Sent = [];
        public async Task Send(EmailMessage message, string id, CancellationToken cancellationToken)
        {
            if (Failure is not null) throw Failure;
            if (Fail) throw new IOException("simulated SMTP failure");
            SendEntered?.TrySetResult();
            if (SendRelease is not null) await SendRelease.Task.WaitAsync(cancellationToken);
            Sent.Add((message, id));
        }
    }
    private WebApplicationFactory<Program> App(HttpMessageHandler? cloudflareHandler = null) => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        { ["Storage:Path"] = Path.Combine(directory, "test.db"), ["Email:PublicUrl"] = "https://journal.example", ["Tracker:Password"] = "original-secret" }));
        if (cloudflareHandler is not null)
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:Provider"] = "Cloudflare", ["Email:Enabled"] = "true",
                ["Email:Cloudflare:AccountId"] = new string('a', 32), ["Email:Cloudflare:ApiToken"] = "test-token",
                ["Email:From"] = "dodo <summaries@dodojournal.com>"
            }));
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<ILoggerProvider>(logs);
            services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock);
            if (cloudflareHandler is null)
            { services.RemoveAll<IEmailTransport>(); services.AddSingleton<IEmailTransport>(transport); }
            else services.AddHttpClient("CloudflareEmail").ConfigurePrimaryHttpMessageHandler(() => cloudflareHandler);
            // Drive the real queue deterministically, without a background poll racing test operations.
            var worker = services.Single(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(EmailWorker));
            services.Remove(worker);
        });
    });
    private sealed class CloudflareHandler : HttpMessageHandler
    {
        public int Attempts;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Attempts == 1 ? "invalid provider JSON" :
                    """{"success":true,"result":{"queued":["alice@example.com"]}}""")
            });
        }
    }
    [Fact]
    public async Task CloudflareProviderUsesRealQueueAndRetriesMalformedApiResponse()
    {
        using var handler = new CloudflareHandler();
        using var app = App(handler); using var user = await Register(app); await Save(user);
        Assert.IsType<CloudflareEmailTransport>(app.Services.GetRequiredService<IEmailTransport>());
        var queue = app.Services.GetRequiredService<EmailQueue>();
        Assert.True(await queue.ProcessOne());
        Assert.Contains(logs.Messages, message => message.Contains("reason cloudflare-invalid-response; stage send; state pending"));
        Assert.False(await queue.ProcessOne());
        clock.Now = clock.Now.AddMinutes(3);
        Assert.True(await queue.ProcessOne());
        Assert.Equal(2, handler.Attempts);
        Assert.Contains(logs.Messages, message => message.Contains("accepted by email provider on attempt 2"));
        Assert.False(await queue.ProcessOne());
    }
    private static async Task Csrf(HttpClient client)
    {
        var json = await client.GetFromJsonAsync<JsonElement>("/api/account/csrf");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", json.GetProperty("token").GetString());
    }
    private static async Task<HttpClient> Register(WebApplicationFactory<Program> app, string name = "Alice", bool existing = false)
    {
        var client = app.CreateClient(); await Csrf(client);
        (await client.PostAsJsonAsync($"/api/account/{(existing ? "login" : "register")}", new { nickname = name, password = "test-password-123" })).EnsureSuccessStatusCode();
        await Csrf(client); return client;
    }
    private static async Task<string> User(HttpClient client) => (await client.GetFromJsonAsync<JsonElement>("/api/account/me")).GetProperty("id").GetString()!;
    private static async Task Save(HttpClient user, string email = "alice@example.com", bool enabled = true) =>
        (await user.PutAsJsonAsync("/api/email/preferences", new { email, enabled })).EnsureSuccessStatusCode();
    private static string Token(EmailMessage message, string kind) => Uri.UnescapeDataString(Regex.Match(message.Text, $"#{kind}=([^\\s]+)").Groups[1].Value);
    private async Task Confirm(WebApplicationFactory<Program> app, HttpClient user)
    {
        await Save(user); Assert.True(await app.Services.GetRequiredService<EmailQueue>().ProcessOne());
        using var anon = app.CreateClient(); await Csrf(anon);
        (await anon.PostAsJsonAsync("/api/email/verify", new { token = Token(transport.Sent.Last().Message, "verify-email") })).EnsureSuccessStatusCode();
    }
    private async Task<SqliteConnection> Open()
    {
        var db = new SqliteConnection($"Data Source={Path.Combine(directory, "test.db")}");
        await db.OpenAsync(); return db;
    }
    private async Task<long> Count(WebApplicationFactory<Program> app, string sql)
    {
        await using var db = await Open();
        using var cmd = db.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
    private static async Task<HttpResponseMessage> Forgot(HttpClient client, string nickname = "Alice") =>
        await client.PostAsJsonAsync("/api/account/forgot-password", new { nickname });
    private static async Task<HttpResponseMessage> Reset(HttpClient client, string token, string password = "new-password-456") =>
        await client.PostAsJsonAsync("/api/account/reset-password", new { token, password });
    private async Task<string> RequestReset(WebApplicationFactory<Program> app, HttpClient anon)
    {
        (await Forgot(anon)).EnsureSuccessStatusCode();
        Assert.True(await app.Services.GetRequiredService<EmailQueue>().ProcessOne());
        return Token(transport.Sent.Last().Message, "reset-password");
    }
    [Fact]
    public async Task RecoveryRequestsHaveSameResponseForMissingUnverifiedAndVerifiedAccounts()
    {
        using var app = App(); using var user = await Register(app); using var anon = app.CreateClient(); await Csrf(anon);
        var missing = await Forgot(anon, "Nobody"); missing.EnsureSuccessStatusCode();
        var noEmail = await Forgot(anon); noEmail.EnsureSuccessStatusCode();
        Assert.Equal(await missing.Content.ReadAsStringAsync(), await noEmail.Content.ReadAsStringAsync());
        await Save(user);
        var unverified = await Forgot(anon); unverified.EnsureSuccessStatusCode();
        Assert.Equal(await missing.Content.ReadAsStringAsync(), await unverified.Content.ReadAsStringAsync());
        Assert.Equal(0, await Count(app, "SELECT COUNT(*) FROM EmailOutbox WHERE Kind='password-reset'"));
        Assert.True(await app.Services.GetRequiredService<EmailQueue>().ProcessOne());
        (await anon.PostAsJsonAsync("/api/email/verify", new { token = Token(transport.Sent.Last().Message, "verify-email") })).EnsureSuccessStatusCode();
        var eligible = await Forgot(anon, "aLiCe"); eligible.EnsureSuccessStatusCode();
        Assert.Equal(await missing.Content.ReadAsStringAsync(), await eligible.Content.ReadAsStringAsync());
        Assert.Equal(1, await Count(app, "SELECT COUNT(*) FROM EmailOutbox WHERE Kind='password-reset'"));
        (await Forgot(anon)).EnsureSuccessStatusCode();
        Assert.Equal(1, await Count(app, "SELECT COUNT(*) FROM EmailOutbox WHERE Kind='password-reset'"));
    }
    [Fact]
    public async Task PasswordResetUsesVerifiedAddressWithoutOptInRevokesSessionsAndNotifiesOwner()
    {
        using var app = App(); using var user = await Register(app); using var other = await Register(app, "Bobby");
        await Confirm(app, user); await Save(user, enabled: false);
        using var anon = app.CreateClient(); await Csrf(anon);
        var token = await RequestReset(app, anon);
        Assert.Equal("alice@example.com", transport.Sent.Last().Message.To);
        await using (var db = await Open())
        {
            using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT TokenHash FROM PasswordResets";
            var hash = (string)(await cmd.ExecuteScalarAsync())!;
            Assert.Equal(64, hash.Length); Assert.NotEqual(token, hash);
        }
        (await Reset(anon, token)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await Reset(anon, token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/account/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/entries/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/account/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/account/login", new { nickname = "Alice", password = "test-password-123" })).StatusCode);
        (await anon.PostAsJsonAsync("/api/account/login", new { nickname = "Alice", password = "new-password-456" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/api/account/me")).StatusCode);
        Assert.True(await app.Services.GetRequiredService<EmailQueue>().ProcessOne());
        Assert.Equal("Your dodo password was changed", transport.Sent.Last().Message.Subject);
        Assert.DoesNotContain("new-password-456", transport.Sent.Last().Message.Text);
        Assert.DoesNotContain("new-password-456", transport.Sent.Last().Message.Html);
        Assert.Equal(1, await Count(app, "SELECT SUM(SessionVersion) FROM Users"));
        Assert.Equal(0, await Count(app, "SELECT COUNT(*) FROM PasswordResets WHERE TokenHash IS NOT NULL"));
        Assert.All(logs.Messages, message =>
        { Assert.DoesNotContain(token, message); Assert.DoesNotContain("new-password-456", message); Assert.DoesNotContain("alice@example.com", message); });
    }
    [Fact]
    public async Task ExpiredResetCannotChangePasswordAndExpiredQueuedResetIsCancelled()
    {
        using var app = App(); using var user = await Register(app); await Confirm(app, user);
        using var anon = app.CreateClient(); await Csrf(anon);
        var token = await RequestReset(app, anon);
        clock.Now = clock.Now.AddMinutes(30);
        Assert.Equal(HttpStatusCode.BadRequest, (await Reset(anon, token)).StatusCode);
        (await Forgot(anon)).EnsureSuccessStatusCode();
        clock.Now = clock.Now.AddMinutes(31);
        Assert.Equal(HttpStatusCode.BadRequest, (await Reset(anon, token)).StatusCode);
        Assert.False(await app.Services.GetRequiredService<EmailQueue>().ProcessOne());
        Assert.Equal(1, await Count(app, "SELECT COUNT(*) FROM EmailOutbox WHERE Kind='password-reset' AND State='cancelled'"));
        Assert.Equal(0, await Count(app, "SELECT SUM(SessionVersion) FROM Users"));
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/account/me")).StatusCode);
    }
    [Fact]
    public async Task NewResetLinkInvalidatesOldOneAndConcurrentConsumptionWorksOnce()
    {
        using var app = App(); using var user = await Register(app); await Confirm(app, user);
        using var anon = app.CreateClient(); await Csrf(anon);
        var old = await RequestReset(app, anon);
        // Simulate an old send whose lease must be reclaimed after a newer request supersedes it.
        await using (var db = await Open())
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE EmailOutbox SET State='sending',LeaseUntil=0 WHERE Kind='password-reset'";
            await cmd.ExecuteNonQueryAsync();
        }
        clock.Now = clock.Now.AddSeconds(61);
        var current = await RequestReset(app, anon);
        Assert.NotEqual(old, current);
        Assert.Equal(1, await Count(app, "SELECT COUNT(*) FROM EmailOutbox WHERE Kind='password-reset' AND State='cancelled'"));
        Assert.Equal(HttpStatusCode.BadRequest, (await Reset(anon, old)).StatusCode);
        var results = await Task.WhenAll(Reset(anon, current), Reset(anon, current));
        Assert.Single(results, response => response.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(results, response => response.StatusCode == HttpStatusCode.BadRequest);
        Assert.Equal(1, await Count(app, "SELECT SUM(SessionVersion) FROM Users"));
    }
    [Fact]
    public async Task ChangingSavedEmailInvalidatesRecoveryAndCancelsQueuedReset()
    {
        using var app = App(); using var user = await Register(app); await Confirm(app, user);
        using var anon = app.CreateClient(); await Csrf(anon);
        var token = await RequestReset(app, anon);
        clock.Now = clock.Now.AddSeconds(61); (await Forgot(anon)).EnsureSuccessStatusCode();
        await Save(user, "new@example.com");
        Assert.Equal(HttpStatusCode.BadRequest, (await Reset(anon, token)).StatusCode);
        (await Forgot(anon)).EnsureSuccessStatusCode();
        Assert.Equal(2, await Count(app, "SELECT COUNT(*) FROM EmailOutbox WHERE Kind='password-reset'"));
        Assert.Equal(1, await Count(app, "SELECT COUNT(*) FROM EmailOutbox WHERE Kind='password-reset' AND State='cancelled' AND Payload=''"));
        Assert.Equal(0, await Count(app, "SELECT COUNT(*) FROM PasswordResets"));
    }
    [Fact]
    public async Task RecoveryRequiresCsrfAndPasswordPolicyWithoutConsumingValidTokenOnError()
    {
        using var app = App(); using var user = await Register(app); await Confirm(app, user);
        using var anon = app.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await Forgot(anon)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Reset(anon, "invalid")).StatusCode);
        await Csrf(anon);
        var token = await RequestReset(app, anon);
        Assert.Equal(HttpStatusCode.BadRequest, (await Reset(anon, token, "short")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Reset(anon, token, new string('x', 129))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsJsonAsync("/api/email/verify", new { token })).StatusCode);
        (await Reset(anon, token)).EnsureSuccessStatusCode();
    }
    [Fact]
    public async Task RecoveryUnavailableReturnsGenericResponseWithoutQueuingMail()
    {
        using var app = App(); using var user = await Register(app); await Confirm(app, user);
        transport.Available = false;
        using var anon = app.CreateClient(); await Csrf(anon);
        var response = await Forgot(anon); response.EnsureSuccessStatusCode();
        Assert.Contains(PasswordResetStore.RequestMessage, await response.Content.ReadAsStringAsync());
        Assert.Equal(0, await Count(app, "SELECT COUNT(*) FROM EmailOutbox WHERE Kind='password-reset'"));
    }
    [Fact]
    public async Task CookiesWithoutSessionVersionRemainValidUntilPasswordReset()
    {
        using var app = App(); using var user = await Register(app); await Confirm(app, user);
        var id = await User(user);
        var options = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, id)], CookieAuthenticationDefaults.AuthenticationScheme)),
            new AuthenticationProperties { IssuedUtc = clock.Now, ExpiresUtc = clock.Now.AddDays(7) }, CookieAuthenticationDefaults.AuthenticationScheme);
        using var legacy = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        legacy.DefaultRequestHeaders.Add("Cookie", $"dod-session={options.TicketDataFormat.Protect(ticket)}");
        Assert.Equal(HttpStatusCode.OK, (await legacy.GetAsync("/api/account/me")).StatusCode);
        using var anon = app.CreateClient(); await Csrf(anon);
        var token = await RequestReset(app, anon); (await Reset(anon, token)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await legacy.GetAsync("/api/account/me")).StatusCode);
    }
    [Fact]
    public async Task VersionSixUpgradePreservesVerifiedEmailAndPasswordResetSurvivesRestart()
    {
        using (var original = App())
        using (var user = await Register(original)) await Confirm(original, user);
        // Model the pre-recovery schema while retaining existing account, email and key data.
        await using (var db = await Open())
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DROP TABLE PasswordResets; ALTER TABLE Users DROP COLUMN SessionVersion; PRAGMA user_version=6;";
            await cmd.ExecuteNonQueryAsync();
        }
        string token;
        using (var upgraded = App())
        using (var user = await Register(upgraded, existing: true))
        using (var anon = upgraded.CreateClient())
        {
            Assert.True((await user.GetFromJsonAsync<EmailPreferences>("/api/email/preferences"))!.Verified);
            Assert.Equal(7, await Count(upgraded, "PRAGMA user_version"));
            await Csrf(anon); token = await RequestReset(upgraded, anon);
        }
        using var restarted = App(); using var resetClient = restarted.CreateClient(); await Csrf(resetClient);
        (await Reset(resetClient, token)).EnsureSuccessStatusCode();
        (await resetClient.PostAsJsonAsync("/api/account/login", new { nickname = "Alice", password = "new-password-456" })).EnsureSuccessStatusCode();
    }
    [Fact]
    public async Task TestSummaryRequiresSessionCsrfVerifiedAddressAndConfiguredDelivery()
    {
        using var app = App(); using var anon = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/email/test-summary", null)).StatusCode);
        using var user = await Register(app);
        user.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostAsync("/api/email/test-summary", null)).StatusCode);
        await Csrf(user); await Save(user);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostAsync("/api/email/test-summary", null)).StatusCode);
        Assert.Equal(0, await Count(app, "SELECT COUNT(*) FROM EmailOutbox WHERE Kind='test'"));
        transport.Available = false;
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostAsync("/api/email/test-summary", null)).StatusCode);
    }
    [Fact]
    public async Task TestSummaryGoesToOwnAddressWithoutOptInAndDoesNotConsumeWeeklySlot()
    {
        using var app = App(); using var user = await Register(app); await Confirm(app, user);
        await Save(user, enabled: false);
        (await user.PostAsync("/api/email/test-summary", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostAsync("/api/email/test-summary", null)).StatusCode);
        Assert.Equal(1, await Count(app, "SELECT COUNT(*) FROM EmailOutbox WHERE Kind='test'"));
        Assert.True(await app.Services.GetRequiredService<EmailQueue>().ProcessOne());
        var message = transport.Sent.Last().Message;
        Assert.Equal("alice@example.com", message.To);
        Assert.StartsWith("[Test]", message.Subject);
        Assert.Contains(clock.Week.AddDays(-7).ToString("yyyy-MM-dd"), message.Subject);
        Assert.Contains("Current week preview", message.Html);
        Assert.Contains("no full week of data is required", message.Text);
        Assert.Contains("Stop weekly emails", message.Text);
        Assert.Equal(0, await Count(app, "SELECT COUNT(*) FROM EmailOutbox WHERE Kind='weekly'"));
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostAsync("/api/email/test-summary", null)).StatusCode);
        clock.Now = clock.Now.AddSeconds(61);
        (await user.PostAsync("/api/email/test-summary", null)).EnsureSuccessStatusCode();
        Assert.True(await app.Services.GetRequiredService<EmailQueue>().ProcessOne());
    }
    [Fact]
    public async Task TestSummaryIsCancelledWhenSavedAddressChangesBeforeDelivery()
    {
        using var app = App(); using var user = await Register(app); await Confirm(app, user);
        (await user.PostAsync("/api/email/test-summary", null)).EnsureSuccessStatusCode();
        clock.Now = clock.Now.AddMinutes(2);
        await Save(user, "new@example.com");
        await app.Services.GetRequiredService<EmailQueue>().ProcessOne();
        Assert.Equal(1, await Count(app, "SELECT COUNT(*) FROM EmailOutbox WHERE Kind='test' AND State='cancelled' AND Payload=''"));
        Assert.DoesNotContain(transport.Sent, sent => sent.Message.Subject.StartsWith("[Test]"));
    }
    [Fact]
    public async Task PreferencesRequireSessionAndCsrfAndVerificationTokensAreSingleUse()
    {
        using var app = App(); using var user = await Register(app); using var anon = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/email/preferences")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PutAsJsonAsync("/api/email/preferences", new { email = "x@example.com", enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PutAsJsonAsync("/api/email/preferences", new { email = "bad\r\nBcc:x@example.com", enabled = true })).StatusCode);
        await Save(user);
        Assert.False((await user.GetFromJsonAsync<EmailPreferences>("/api/email/preferences"))!.Verified);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostAsync("/api/email/resend", null)).StatusCode);
        var queue = app.Services.GetRequiredService<EmailQueue>();
        Assert.True(await queue.ProcessOne()); Assert.False(await queue.ProcessOne());
        var token = Token(Assert.Single(transport.Sent).Message, "verify-email"); Assert.NotEmpty(token);
        await Csrf(anon);
        (await anon.PostAsJsonAsync("/api/email/verify", new { token })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsJsonAsync("/api/email/verify", new { token })).StatusCode);
        Assert.True((await user.GetFromJsonAsync<EmailPreferences>("/api/email/preferences"))!.Verified);
        Assert.Equal(0, await Count(app, "SELECT COUNT(*) FROM EmailOutbox WHERE Payload<>''"));
    }
    [Fact]
    public async Task AddressChangeCancelsOldTokensAndUnsubscribeCannotDisableNewAddress()
    {
        using var app = App(); using var user = await Register(app); await Confirm(app, user);
        var prefs = app.Services.GetRequiredService<EmailPreferencesStore>(); var id = await User(user);
        string revision;
        await using (var db = await Open())
        {
            using var cmd = WeeklyRules.Command(db, null, "SELECT Revision FROM EmailPreferences WHERE UserId=$id", ("$id", id));
            revision = (string)(await cmd.ExecuteScalarAsync())!;
        }
        var oldUnsubscribe = prefs.UnsubscribeUrl(id, revision).Split("#unsubscribe=")[1];
        clock.Now = clock.Now.AddMinutes(2); await Save(user, "new@example.com");
        Assert.False(await prefs.Unsubscribe(Uri.UnescapeDataString(oldUnsubscribe)));
        Assert.False((await prefs.Get(id)).Verified);
        Assert.True((await prefs.Get(id)).Enabled);
        await app.Services.GetRequiredService<EmailQueue>().ProcessOne();
        var token = Token(transport.Sent.Last().Message, "verify-email");
        clock.Now = clock.Now.AddDays(2); Assert.False(await prefs.Verify(token));
    }
    [Fact]
    public async Task WeeklyScheduleIsIdempotentAndBonusCorrectionsChangeXp()
    {
        using var app = App(); using var user = await Register(app); await Confirm(app, user);
        clock.Now = Clock.At(clock.Week.AddDays(6), 11);
        for (var day = clock.Week; day <= clock.Week.AddDays(6); day = day.AddDays(1))
        {
            (await user.PutAsJsonAsync($"/api/entries/{day:yyyy-MM-dd}/weight", new { weightKg = 90 - day.DayNumber / 10m + clock.Week.DayNumber / 10m })).EnsureSuccessStatusCode();
            foreach (var activity in new[] { "no-sweets", "no-late-food" })
                (await user.PutAsJsonAsync($"/api/motivation/{day:yyyy-MM-dd}/activities/{activity}", new { completed = true })).EnsureSuccessStatusCode();
        }
        var queue = app.Services.GetRequiredService<EmailQueue>();
        clock.Now = Clock.At(clock.Week.AddDays(7), 7, 59); await queue.Schedule();
        Assert.Equal(1, await Count(app, "SELECT COUNT(*) FROM WeeklyAwards WHERE Points=50"));
        Assert.Equal(0, await Count(app, "SELECT COUNT(*) FROM EmailOutbox WHERE Kind='weekly'"));
        clock.Now = clock.Now.AddMinutes(1); using var renewed = await Register(app, existing: true); await Task.WhenAll(queue.Schedule(), queue.Schedule());
        Assert.Equal(1, await Count(app, "SELECT COUNT(*) FROM EmailOutbox WHERE Kind='weekly'"));
        Assert.True(await queue.ProcessOne());
        var recap = transport.Sent.Last().Message; Assert.Contains("50 XP weekly bonus", recap.Html);
        var summary = (await renewed.GetFromJsonAsync<WeeklySummary>($"/api/motivation/weekly/{clock.Week:yyyy-MM-dd}"))!;
        Assert.Equal(50, summary.BonusXp); Assert.Equal(260, summary.Experience.TotalXp); Assert.Equal(0.6m, summary.KgLost);
        var change = (await (await renewed.PutAsJsonAsync($"/api/motivation/{clock.Week:yyyy-MM-dd}/activities/no-sweets", new { completed = false })).Content.ReadFromJsonAsync<XpChange>())!;
        Assert.Equal(-60, change.Awarded); Assert.Equal(200, change.Experience.TotalXp);
        using var anon = app.CreateClient(); await Csrf(anon);
        (await anon.PostAsJsonAsync("/api/email/unsubscribe", new { token = Token(recap, "unsubscribe") })).EnsureSuccessStatusCode();
        Assert.False((await renewed.GetFromJsonAsync<EmailPreferences>("/api/email/preferences"))!.Enabled);
    }
    [Fact]
    public async Task RetrySurvivesRestartAndUsesStableMessageId()
    {
        string? id = null;
        using (var app = App())
        using (var user = await Register(app))
        {
            await Save(user); transport.Fail = true;
            Assert.True(await app.Services.GetRequiredService<EmailQueue>().ProcessOne());
            Assert.False(await app.Services.GetRequiredService<EmailQueue>().ProcessOne());
            await using var db = await Open();
            using var cmd = WeeklyRules.Command(db, null, "SELECT Id FROM EmailOutbox WHERE State='pending' AND Attempts=1");
            id = (string)(await cmd.ExecuteScalarAsync())!;
        }
        clock.Now = clock.Now.AddMinutes(3); transport.Fail = false;
        using var restarted = App(); using var signed = await Register(restarted, existing: true);
        Assert.True(await restarted.Services.GetRequiredService<EmailQueue>().ProcessOne());
        Assert.Equal(id, Assert.Single(transport.Sent).Id);
        Assert.Equal(0, await Count(restarted, "SELECT COUNT(*) FROM EmailOutbox WHERE Payload<>''"));
    }
    [Fact]
    public async Task DisabledDeliveryCanSavePreferencesAndUnsubscribeCancelsQueuedRecap()
    {
        transport.Available = false;
        using var app = App(); using var user = await Register(app);
        await Save(user); Assert.Equal(0, await Count(app, "SELECT COUNT(*) FROM EmailOutbox"));
        transport.Available = true; await Confirm(app, user);
        clock.Now = Clock.At(clock.Week.AddDays(7), 8); var queue = app.Services.GetRequiredService<EmailQueue>();
        await queue.Schedule(); using var renewed = await Register(app, existing: true); await Save(renewed, enabled: false);
        Assert.False(await queue.ProcessOne());
        Assert.Equal(1, await Count(app, "SELECT COUNT(*) FROM EmailOutbox WHERE Kind='weekly' AND State='cancelled' AND Payload=''"));
    }
    [Fact]
    public async Task ActiveLeaseExcludesOtherConsumersAndExpiredLeaseIsRecovered()
    {
        using var app = App(); using var user = await Register(app); await Save(user);
        var queue = app.Services.GetRequiredService<EmailQueue>();
        transport.SendEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.SendRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = queue.ProcessOne();
        await transport.SendEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(await queue.ProcessOne());
        transport.SendRelease.SetResult(); Assert.True(await first); Assert.Single(transport.Sent);
        clock.Now = clock.Now.AddMinutes(2); await user.PostAsync("/api/email/resend", null);
        await using (var db = await Open())
        {
            using var cmd = WeeklyRules.Command(db, null, "UPDATE EmailOutbox SET State='sending',LeaseUntil=$lease WHERE State='pending'", ("$lease", clock.Now.ToUnixTimeSeconds() + 120));
            await cmd.ExecuteNonQueryAsync();
        }
        Assert.False(await queue.ProcessOne()); clock.Now = clock.Now.AddMinutes(3);
        Assert.True(await queue.ProcessOne()); Assert.Equal(2, transport.Sent.Count);
    }
    [Fact]
    public async Task SixFailuresBecomeTerminalAndEraseProtectedPayload()
    {
        using var app = App(); using var user = await Register(app); await Save(user); transport.Fail = true;
        var queue = app.Services.GetRequiredService<EmailQueue>();
        for (var attempt = 0; attempt < 6; attempt++) { Assert.True(await queue.ProcessOne()); clock.Now = clock.Now.AddHours(1); }
        Assert.False(await queue.ProcessOne());
        Assert.Equal(1, await Count(app, "SELECT COUNT(*) FROM EmailOutbox WHERE State='failed' AND Attempts=6 AND Payload=''"));
    }
    [Fact]
    public async Task BonusesDoNotDependOnEmailAndDoNotBackfillBeforeEligibility()
    {
        transport.Available = false;
        using var app = App(); using var user = await Register(app); var id = await User(user);
        await using (var db = await Open())
        {
            using var cmd = WeeklyRules.Command(db, null, """
                WITH RECURSIVE days(day) AS (SELECT $start UNION ALL SELECT date(day,'+1 day') FROM days WHERE day<$end)
                INSERT INTO ActivityReports(UserId,ActivityId,Date,Completed,Points,Name,Description,CutoffTime)
                SELECT $user,a.Id,d.day,1,a.Points,a.Name,a.Description,a.CutoffTime FROM Activities a CROSS JOIN days d
                """, ("$start", WeeklyRules.Day(clock.Week.AddDays(-7))), ("$end", WeeklyRules.Day(clock.Week.AddDays(6))), ("$user", id));
            await cmd.ExecuteNonQueryAsync();
        }
        clock.Now = Clock.At(clock.Week.AddDays(7), 8);
        await app.Services.GetRequiredService<EmailQueue>().Schedule();
        Assert.Equal(1, await Count(app, "SELECT COUNT(*) FROM WeeklyAwards WHERE Points=50"));
        Assert.Equal(0, await Count(app, "SELECT COUNT(*) FROM EmailOutbox"));
    }
    [Fact]
    public async Task SummaryIsPrivateAndChallengeScoresStopAtSunday()
    {
        using var app = App(); using var alice = await Register(app); using var bob = await Register(app, "Bobby");
        var aliceId = await User(alice); var bobId = await User(bob);
        await using (var db = await Open())
        {
            using var cmd = WeeklyRules.Command(db, null, """
                INSERT INTO Challenges VALUES ('shared',$bob,'Shared challenge',$start,$end),('private',$bob,'Private challenge',$start,$end),('invited',$bob,'Invitation',$start,$end);
                INSERT INTO Participants VALUES ('shared',$alice,$bob,'accepted'),('shared',$bob,$bob,'accepted'),('private',$bob,$bob,'accepted'),('invited',$alice,$bob,'pending'),('invited',$bob,$bob,'accepted');
                INSERT INTO Entries(UserId,Date,WeightKg) VALUES ($alice,$start,80),($alice,$sunday,79),($alice,$monday,70),($bob,$start,90),($bob,$sunday,88);
                """, ("$alice", aliceId), ("$bob", bobId), ("$start", WeeklyRules.Day(clock.Week)), ("$sunday", WeeklyRules.Day(clock.Week.AddDays(6))),
                ("$monday", WeeklyRules.Day(clock.Week.AddDays(7))), ("$end", WeeklyRules.Day(clock.Week.AddDays(14))));
            await cmd.ExecuteNonQueryAsync();
        }
        clock.Now = Clock.At(clock.Week.AddDays(7), 8);
        using var renewed = await Register(app, existing: true);
        var summary = (await renewed.GetFromJsonAsync<WeeklySummary>($"/api/motivation/weekly/{clock.Week:yyyy-MM-dd}"))!;
        Assert.Equal(1m, summary.KgLost);
        var shared = Assert.Single(summary.Challenges, c => c.Name == "Shared challenge");
        Assert.Equal(2, shared.Rank); Assert.Equal(1m, shared.KgLost); Assert.Equal(2, shared.Participants);
        Assert.DoesNotContain(summary.Challenges, c => c.Name == "Private challenge");
        Assert.Null(Assert.Single(summary.Challenges, c => c.Name == "Invitation").Rank);
        Assert.Null(Assert.Single(summary.Challenges, c => c.Name == "Invitation").KgLost);
    }
    private sealed class EnvironmentStub(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    [Theory]
    [InlineData("Production", "https://journal.example", "StartTls", 587, true)]
    [InlineData("Production", "https://journal.example", "SslOnConnect", 465, true)]
    [InlineData("Production", "https://journal.example", "None", 25, false)]
    [InlineData("Production", "http://journal.example", "StartTls", 587, false)]
    [InlineData("Production", "https://journal.example/?secret=x", "StartTls", 587, false)]
    [InlineData("Production", "https://journal.example/subpath", "StartTls", 587, false)]
    [InlineData("Production", "https://journal.example", "StartTls", 0, false)]
    [InlineData("Development", "http://localhost:8025", "None", 1025, true)]
    public void SmtpAvailabilityRequiresSafeProductionSettings(string environment, string url, string security, int port, bool expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Email:Enabled"] = "true", ["Email:Host"] = "smtp.example", ["Email:From"] = "dodo <sender@example.com>",
            ["Email:PublicUrl"] = url, ["Email:Security"] = security, ["Email:Port"] = port.ToString()
        }).Build();
        Assert.Equal(expected, new SmtpEmailTransport(config, new EnvironmentStub(environment)).Available);
        config["Email:Enabled"] = "false";
        Assert.False(new SmtpEmailTransport(config, new EnvironmentStub(environment)).Available);
    }
    [Fact]
    public void HtmlSummaryEscapesUserTextAndProvidesPlainTextAlternative()
    {
        var summary = new WeeklySummary("2026-09-07", "2026-09-13", "2026-09-07", true, 0, 10, Experience.From(10), -1m, 2,
            [new("test", "<img src=x onerror=alert(1)>", [new("2026-09-07", true, 10)])],
            [new("<script>alert(1)</script>", "Active", 1, 2, 1m)]);
        var message = EmailTemplates.Weekly("alice@example.com", "Alice", summary, "https://journal.example", "https://journal.example/#unsubscribe=x");
        Assert.DoesNotContain("<img", message.Html); Assert.DoesNotContain("<script", message.Html);
        Assert.Contains("&lt;script&gt;", message.Html); Assert.Contains("1.00 kg gained", message.Text);
        Assert.Contains("Stop weekly emails:", message.Text); Assert.Contains("Completed (10 XP)", message.Text);
    }
    private sealed class QueueLogs : ILoggerProvider
    {
        public List<string> Messages { get; } = [];
        public ILogger CreateLogger(string categoryName) => new QueueLogger(categoryName, Messages);
        public void Dispose() { }
        private sealed class QueueLogger(string category, List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (category == typeof(EmailQueue).FullName || category == typeof(PasswordResetStore).FullName)
                    messages.Add(formatter(state, exception) + exception?.Message);
            }
        }
    }
    [Fact]
    public async Task QueueLogsExposeSafeFailureReasonAndAcceptanceWithoutSensitiveDetails()
    {
        using var app = App(); using var user = await Register(app); await Save(user);
        var queue = app.Services.GetRequiredService<EmailQueue>();
        const string secret = "private-token alice@example.com";
        transport.Failure = new MailKit.Security.AuthenticationException(secret);
        Assert.True(await queue.ProcessOne());
        Assert.Contains(logs.Messages, message => message.Contains("reason smtp-authentication"));
        Assert.All(logs.Messages, message => { Assert.DoesNotContain(secret, message); Assert.DoesNotContain("alice@example.com", message); });
        transport.Failure = new InvalidOperationException(secret); clock.Now = clock.Now.AddMinutes(3);
        Assert.True(await queue.ProcessOne());
        Assert.Contains(logs.Messages, message => message.Contains("unexpected-System.InvalidOperationException; stage transport"));
        Assert.All(logs.Messages, message => { Assert.DoesNotContain(secret, message); Assert.DoesNotContain("alice@example.com", message); });
        transport.Failure = null; clock.Now = clock.Now.AddMinutes(5);
        Assert.True(await queue.ProcessOne());
        Assert.Contains(logs.Messages, message => message.Contains("accepted by email provider on attempt 3"));
    }
    [Fact]
    public async Task SmtpDiagnosticsIdentifyAuthenticationStageWhenServerDoesNotOfferAuth()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = socket.GetStream();
            using var reader = new StreamReader(stream);
            await using var writer = new StreamWriter(stream) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 localhost Test SMTP");
            Assert.StartsWith("EHLO ", await reader.ReadLineAsync(timeout.Token));
            await writer.WriteLineAsync("250 localhost");
        }, timeout.Token);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Email:Enabled"] = "true", ["Email:Host"] = "127.0.0.1", ["Email:Port"] = port.ToString(),
            ["Email:Security"] = "None", ["Email:PublicUrl"] = "http://localhost", ["Email:From"] = "sender@example.com",
            ["Email:Username"] = "api_token", ["Email:Password"] = "test-only-secret"
        }).Build();
        var transport = new SmtpEmailTransport(config, new EnvironmentStub("Development"));
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => transport.Send(
            new("recipient@example.com", "Test", "<p>Test</p>", "Test"), "test-id", timeout.Token));
        await server;
        Assert.Equal("authenticate", EmailFailure.Stage(error));
        Assert.Equal("unexpected-System.NotSupportedException", EmailFailure.Code(error));
        Assert.DoesNotContain("test-only-secret", EmailFailure.Code(error));
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
