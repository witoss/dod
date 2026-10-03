using System.Security.Cryptography;
using System.Text;
using Dod.Api.Entries;
using Dod.Api.Notifications;
using Microsoft.AspNetCore.WebUtilities;
using static Dod.Api.Motivation.Persistence.WeeklyRules;

namespace Dod.Api.Accounts;

public record PasswordResetRequest(string Nickname);
public record PasswordResetInput(string Token, string Password);

public sealed class PasswordResetStore(EntryStore entries, EmailPreferencesStore preferences,
    IEmailTransport transport, TimeProvider clock, ILogger<PasswordResetStore> logger)
{
    public const string RequestMessage = "If this account has a confirmed email address, a password reset link will be sent to it.";
    private long Now => clock.GetUtcNow().ToUnixTimeSeconds();
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public async Task Request(string? nickname)
    {
        if (!transport.Available) return;
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        await using var db = await entries.OpenAsync(); using var tx = db.BeginTransaction();
        string user, email, revision;
        using (var cmd = Command(db, tx, """
            SELECT u.Id,p.Email,p.Revision FROM Users u JOIN EmailPreferences p ON p.UserId=u.Id
            LEFT JOIN PasswordResets r ON r.UserId=u.Id
            WHERE u.NormalizedNickname=$nick AND u.PasswordHash IS NOT NULL AND p.Verified=1
                AND (r.LastRequested IS NULL OR r.LastRequested<=$recent)
            """, ("$nick", AccountEndpoints.ValidNickname(nickname) ? AccountEndpoints.Normalize(nickname!) : "!"), ("$recent", Now - 60)))
        using (var rows = await cmd.ExecuteReaderAsync())
        {
            if (!await rows.ReadAsync()) return;
            user = rows.GetString(0); email = rows.GetString(1); revision = rows.GetString(2);
        }
        using (var cmd = Command(db, tx, """
            INSERT INTO PasswordResets(UserId,EmailRevision,TokenHash,Expires,LastRequested) VALUES($user,$revision,$hash,$expires,$now)
            ON CONFLICT(UserId) DO UPDATE SET EmailRevision=excluded.EmailRevision,TokenHash=excluded.TokenHash,
                Expires=excluded.Expires,LastRequested=excluded.LastRequested;
            UPDATE EmailOutbox SET State='cancelled',Payload='' WHERE UserId=$user AND Kind='password-reset' AND State='pending';
            """, ("$user", user), ("$revision", revision), ("$hash", Hash(token)), ("$expires", Now + 1800), ("$now", Now)))
            await cmd.ExecuteNonQueryAsync();
        await preferences.Enqueue(db, tx, user, revision, "password-reset", null,
            EmailTemplates.PasswordReset(email, $"{preferences.PublicUrl}/#reset-password={token}"));
        using (var cmd = Command(db, tx, """
            UPDATE PasswordResets SET OutboxId=(SELECT Id FROM EmailOutbox WHERE UserId=$user
                AND Revision=$revision AND Kind='password-reset' AND State='pending') WHERE UserId=$user
            """, ("$user", user), ("$revision", revision)))
            await cmd.ExecuteNonQueryAsync();
        tx.Commit();
        logger.LogInformation("Password reset email queued for user {UserId}", user);
    }

    public async Task<bool> Reset(string? token, string password)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 200 || password.Length is < 12 or > 128) return false;
        await using var db = await entries.OpenAsync(); using var tx = db.BeginTransaction();
        string user, email, revision;
        using (var cmd = Command(db, tx, """
            SELECT r.UserId,p.Email,p.Revision FROM PasswordResets r JOIN EmailPreferences p ON p.UserId=r.UserId
            JOIN Users u ON u.Id=r.UserId WHERE r.TokenHash=$hash AND r.Expires>$now
                AND p.Verified=1 AND p.Revision=r.EmailRevision AND u.PasswordHash IS NOT NULL
            """, ("$hash", Hash(token)), ("$now", Now)))
        using (var rows = await cmd.ExecuteReaderAsync())
        {
            if (!await rows.ReadAsync()) return false;
            user = rows.GetString(0); email = rows.GetString(1); revision = rows.GetString(2);
        }
        using (var cmd = Command(db, tx, """
            UPDATE Users SET PasswordHash=$password,SessionVersion=SessionVersion+1 WHERE Id=$user;
            UPDATE PasswordResets SET TokenHash=NULL,Expires=0 WHERE UserId=$user;
            UPDATE EmailOutbox SET State='cancelled',Payload='' WHERE UserId=$user AND Kind='password-reset' AND State='pending';
            """, ("$user", user), ("$password", AccountEndpoints.Hasher.HashPassword(user, password))))
            await cmd.ExecuteNonQueryAsync();
        await preferences.Enqueue(db, tx, user, revision, "password-changed", null, EmailTemplates.PasswordChanged(email));
        tx.Commit();
        logger.LogInformation("Password reset completed for user {UserId}; existing sessions revoked", user);
        return true;
    }
}
