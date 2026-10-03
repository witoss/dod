using Dod.Api.Motivation.Persistence;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dod.Api.Entries;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using static Dod.Api.Motivation.Persistence.WeeklyRules;

namespace Dod.Api.Notifications;

public record EmailPreferences(string Email, bool Enabled, bool Verified, bool SendingAvailable);
public record EmailInput(string Email, bool Enabled);
public record TokenInput(string Token);
public sealed class EmailPreferencesStore(EntryStore entries, TimeProvider clock, IEmailTransport transport,
    IDataProtectionProvider protection, IConfiguration config)
{
    private readonly IDataProtector payloadProtector = protection.CreateProtector("dodo.email.outbox.v1");
    private readonly IDataProtector unsubscribeProtector = protection.CreateProtector("dodo.email.unsubscribe.v1");
    public string PublicUrl => (config["Email:PublicUrl"] ?? "").TrimEnd('/');
    private long Now => clock.GetUtcNow().ToUnixTimeSeconds();
    public static bool ValidEmail(string? email) => email is not null && email.Length <= 254 && !email.Contains('\r') && !email.Contains('\n')
        && MailAddress.TryCreate(email, out var address) && address.DisplayName.Length == 0 && address.Address == email && email.Contains('@');
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public async Task<EmailPreferences> Get(string user)
    {
        await using var db = await entries.OpenAsync();
        using var cmd = Command(db, null, "SELECT Email,Enabled,Verified FROM EmailPreferences WHERE UserId=$user", ("$user", user));
        using var rows = await cmd.ExecuteReaderAsync();
        return await rows.ReadAsync() ? new(rows.GetString(0), rows.GetBoolean(1), rows.GetBoolean(2), transport.Available) : new("", false, false, transport.Available);
    }
    public async Task<string?> Save(string user, EmailInput input, bool resend = false)
    {
        var email = (input.Email ?? "").Trim();
        if (email.Length > 0 && !ValidEmail(email)) return "Enter a valid email address.";
        if (input.Enabled && email.Length == 0) return "Enter an email address before enabling notifications.";
        await using var db = await entries.OpenAsync(); using var tx = db.BeginTransaction();
        string? oldEmail = null; string revision = Guid.NewGuid().ToString("N"); bool verified = false; long last = 0;
        using (var cmd = Command(db, tx, "SELECT Email,Revision,Verified,LastVerification FROM EmailPreferences WHERE UserId=$user", ("$user", user)))
        using (var rows = await cmd.ExecuteReaderAsync())
            if (await rows.ReadAsync()) { oldEmail = rows.GetString(0); revision = rows.GetString(1); verified = rows.GetBoolean(2); last = rows.GetInt64(3); }
        var changed = !string.Equals(email, oldEmail, StringComparison.Ordinal);
        var send = email.Length > 0 && (changed || resend || (input.Enabled && !verified)) && transport.Available;
        if (resend && (!transport.Available || email.Length == 0)) return "Email sending is not configured, or no email address is saved.";
        if (send && (changed || !verified) && last > Now - 60) return "Please wait one minute before requesting another confirmation email.";
        if (changed) { revision = Guid.NewGuid().ToString("N"); verified = false; }
        using (var cmd = Command(db, tx, """
            INSERT INTO EmailPreferences(UserId,Email,Enabled,Verified,Revision,LastVerification) VALUES($user,$email,$enabled,$verified,$revision,$last)
            ON CONFLICT(UserId) DO UPDATE SET Email=excluded.Email,Enabled=excluded.Enabled,Verified=excluded.Verified,Revision=excluded.Revision,
                TokenHash=CASE WHEN Revision<>excluded.Revision THEN NULL ELSE TokenHash END,
                TokenExpires=CASE WHEN Revision<>excluded.Revision THEN NULL ELSE TokenExpires END
            """, ("$user", user), ("$email", email), ("$enabled", input.Enabled), ("$verified", verified), ("$revision", revision), ("$last", last)))
            await cmd.ExecuteNonQueryAsync();
        if (changed)
        {
            using var invalidate = Command(db, tx, "DELETE FROM PasswordResets WHERE UserId=$user", ("$user", user));
            await invalidate.ExecuteNonQueryAsync();
        }
        if (changed || !input.Enabled)
        {
            using var cancel = Command(db, tx, "UPDATE EmailOutbox SET State='cancelled',Payload='' WHERE UserId=$user AND State='pending' AND (Revision<>$revision OR Kind='weekly')",
                ("$user", user), ("$revision", revision));
            await cancel.ExecuteNonQueryAsync();
        }
        if (send && !verified)
        {
            var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            using var cmd = Command(db, tx, """
                UPDATE EmailPreferences SET TokenHash=$hash,TokenExpires=$expires,LastVerification=$now WHERE UserId=$user;
                UPDATE EmailOutbox SET State='cancelled',Payload='' WHERE UserId=$user AND Kind='verification' AND State='pending';
                """, ("$user", user), ("$hash", Hash(token)), ("$expires", Now + 86400), ("$now", Now));
            await cmd.ExecuteNonQueryAsync();
            await Enqueue(db, tx, user, revision, "verification", null, EmailTemplates.Verification(email, $"{PublicUrl}/#verify-email={token}"));
        }
        tx.Commit(); return null;
    }
    public async Task<bool> Verify(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 200) return false;
        await using var db = await entries.OpenAsync();
        using var cmd = Command(db, null, "UPDATE EmailPreferences SET Verified=1,TokenHash=NULL,TokenExpires=NULL WHERE TokenHash=$hash AND TokenExpires>$now",
            ("$hash", Hash(token)), ("$now", Now));
        return await cmd.ExecuteNonQueryAsync() == 1;
    }
    public async Task<string?> SendTest(string user, WeeklySummaryStore summaries)
    {
        if (!transport.Available) return "Email sending is not configured.";
        var week = Monday(DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime));
        var summary = await summaries.Build(user, week);
        await using var db = await entries.OpenAsync(); using var tx = db.BeginTransaction();
        string email, revision, nickname;
        using (var cmd = Command(db, tx, """
            SELECT p.Email,p.Revision,u.Nickname FROM EmailPreferences p JOIN Users u ON u.Id=p.UserId
            WHERE p.UserId=$user AND p.Verified=1
            """, ("$user", user)))
        using (var rows = await cmd.ExecuteReaderAsync())
        {
            if (!await rows.ReadAsync()) return "Confirm your saved email address before sending a test summary.";
            email = rows.GetString(0); revision = rows.GetString(1); nickname = rows.GetString(2);
        }
        using (var cmd = Command(db, tx, """
            SELECT COUNT(*) FROM EmailOutbox WHERE UserId=$user AND Kind='test'
            AND (State IN ('pending','sending') OR SentAt>$recent)
            """, ("$user", user), ("$recent", Now - 60)))
            if (Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0)
                return "A test summary is already queued, or was just sent. Please wait before trying again.";
        var message = EmailTemplates.Weekly(email, nickname, summary, PublicUrl, UnsubscribeUrl(user, revision));
        const string preview = "Current week preview: this week is still in progress. Missing entries and future days are not yet complete; no full week of data is required.";
        message = message with
        {
            Subject = $"[Test] {message.Subject}",
            Html = message.Html.Replace("<h1>", $"<p><strong>{preview}</strong></p><h1>"),
            Text = $"{preview}\n\n{message.Text}"
        };
        await Enqueue(db, tx, user, revision, "test", Day(week), message);
        tx.Commit(); return null;
    }
    public string UnsubscribeUrl(string user, string revision) => $"{PublicUrl}/#unsubscribe={Uri.EscapeDataString(unsubscribeProtector.Protect(JsonSerializer.Serialize(new[] { user, revision })))}";
    public async Task<bool> Unsubscribe(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 3000) return false;
        string[]? data;
        try { data = JsonSerializer.Deserialize<string[]>(unsubscribeProtector.Unprotect(token)); }
        catch (Exception e) when (e is CryptographicException or JsonException) { return false; }
        if (data is not { Length: 2 }) return false;
        await using var db = await entries.OpenAsync(); using var tx = db.BeginTransaction();
        using var cmd = Command(db, tx, "UPDATE EmailPreferences SET Enabled=0 WHERE UserId=$user AND Revision=$revision", ("$user", data[0]), ("$revision", data[1]));
        if (await cmd.ExecuteNonQueryAsync() != 1) return false;
        using var cancel = Command(db, tx, "UPDATE EmailOutbox SET State='cancelled',Payload='' WHERE UserId=$user AND Kind='weekly' AND State='pending'", ("$user", data[0]));
        await cancel.ExecuteNonQueryAsync(); tx.Commit(); return true;
    }
    internal async Task Enqueue(SqliteConnection db, SqliteTransaction? tx, string user, string revision, string kind, string? week, EmailMessage message)
    {
        using var cmd = Command(db, tx, """
            INSERT OR IGNORE INTO EmailOutbox(Id,UserId,Revision,Kind,WeekStart,Payload,NextAttempt)
            VALUES($id,$user,$revision,$kind,$week,$payload,$now)
            """, ("$id", Guid.NewGuid().ToString("N")), ("$user", user), ("$revision", revision), ("$kind", kind), ("$week", week),
            ("$payload", payloadProtector.Protect(JsonSerializer.Serialize(message))), ("$now", Now));
        await cmd.ExecuteNonQueryAsync();
    }
    internal EmailMessage Decode(string payload) => JsonSerializer.Deserialize<EmailMessage>(payloadProtector.Unprotect(payload))!;
}
