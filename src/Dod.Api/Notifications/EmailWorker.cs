using Dod.Api.Entries;
using static Dod.Api.Notifications.WeeklyRules;

namespace Dod.Api.Notifications;

// Database state survives restarts. A claim is atomic; the lease outlives the bounded SMTP attempt.
public sealed class EmailQueue(EntryStore entries, EmailPreferencesStore preferences, WeeklySummaryStore summaries,
    IEmailTransport transport, TimeProvider clock, ILogger<EmailQueue> logger)
{
    private long Now => clock.GetUtcNow().ToUnixTimeSeconds();
    public async Task Schedule(CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var monday = Monday(today);
        var users = new List<(string Id, string Nickname, DateOnly From)>();
        await using (var db = await entries.OpenAsync())
        {
            using var cmd = Command(db, null, "SELECT Id,Nickname,WeeklyEligibleFrom FROM Users WHERE PasswordHash IS NOT NULL AND WeeklyEligibleFrom<$week", ("$week", Day(monday)));
            using var rows = await cmd.ExecuteReaderAsync();
            while (await rows.ReadAsync()) users.Add((rows.GetString(0), rows.GetString(1), DateOnly.ParseExact(rows.GetString(2), "yyyy-MM-dd")));
        }
        foreach (var user in users)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var db = await entries.OpenAsync();
            // Awards are independent of email opt-in and SMTP configuration.
            using (var tx = db.BeginTransaction())
            {
                var awarded = new HashSet<string>();
                using (var cmd = Command(db, tx, "SELECT WeekStart FROM WeeklyAwards WHERE UserId=$user", ("$user", user.Id)))
                using (var rows = await cmd.ExecuteReaderAsync())
                    while (await rows.ReadAsync()) awarded.Add(rows.GetString(0));
                for (var week = user.From; week < monday; week = week.AddDays(7))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!awarded.Contains(Day(week))) await Reconcile(db, tx, user.Id, week, today);
                }
                tx.Commit();
            }
            // Send only the most recent closed week, from Monday 08:00 UTC until next Monday.
            // Downtime never produces a flood of old summaries.
            if (!transport.Available || clock.GetUtcNow() < new DateTimeOffset(monday.ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero)) continue;
            var recent = monday.AddDays(-7);
            string? email = null, revision = null;
            using (var cmd = Command(db, null, """
                SELECT p.Email,p.Revision FROM EmailPreferences p WHERE p.UserId=$user AND p.Enabled=1 AND p.Verified=1
                AND NOT EXISTS (SELECT 1 FROM EmailOutbox o WHERE o.UserId=p.UserId AND o.Kind='weekly' AND o.WeekStart=$week)
                """, ("$user", user.Id), ("$week", Day(recent))))
            using (var rows = await cmd.ExecuteReaderAsync())
                if (await rows.ReadAsync()) { email = rows.GetString(0); revision = rows.GetString(1); }
            if (email is null) continue;
            var summary = await summaries.Build(user.Id, recent);
            using (var tx = db.BeginTransaction())
            {
                // Preferences may have changed while preparing the summary.
                using var valid = Command(db, tx, "SELECT COUNT(*) FROM EmailPreferences WHERE UserId=$user AND Revision=$revision AND Enabled=1 AND Verified=1",
                    ("$user", user.Id), ("$revision", revision));
                if (Convert.ToInt32(await valid.ExecuteScalarAsync()) == 1)
                    await preferences.Enqueue(db, tx, user.Id, revision!, "weekly", Day(recent),
                        EmailTemplates.Weekly(email, user.Nickname, summary, preferences.PublicUrl, preferences.UnsubscribeUrl(user.Id, revision!)));
                tx.Commit();
            }
        }
    }
    public async Task<bool> ProcessOne(CancellationToken cancellationToken = default)
    {
        if (!transport.Available) return false;
        await using var db = await entries.OpenAsync();
        string id, payload; int attempt; var lease = Now + 120;
        using (var tx = db.BeginTransaction())
        {
            using var cancel = Command(db, tx, """
                UPDATE EmailOutbox SET State='cancelled',Payload='',LeaseUntil=NULL
                WHERE (State='pending' OR (State='sending' AND LeaseUntil<=$now)) AND (
                    NOT EXISTS (SELECT 1 FROM EmailPreferences p WHERE p.UserId=EmailOutbox.UserId AND p.Revision=EmailOutbox.Revision
                        AND ((EmailOutbox.Kind='weekly' AND p.Enabled=1 AND p.Verified=1)
                            OR (EmailOutbox.Kind='verification' AND p.Verified=0 AND p.TokenHash IS NOT NULL AND p.TokenExpires>$now)))
                    OR (Kind='weekly' AND WeekStart<$oldest));
                """, ("$now", Now), ("$oldest", Day(Monday(DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime)).AddDays(-7))));
            await cancel.ExecuteNonQueryAsync();
            using var claim = Command(db, tx, """
                UPDATE EmailOutbox SET State='sending',LeaseUntil=$lease,Attempts=Attempts+1
                WHERE Id=(SELECT Id FROM EmailOutbox WHERE (State='pending' AND NextAttempt<=$now)
                    OR (State='sending' AND LeaseUntil<=$now) ORDER BY NextAttempt,Id LIMIT 1)
                RETURNING Id,Payload,Attempts
                """, ("$lease", lease), ("$now", Now));
            using (var rows = await claim.ExecuteReaderAsync())
            {
                if (!await rows.ReadAsync()) { tx.Commit(); return false; }
                id = rows.GetString(0); payload = rows.GetString(1); attempt = rows.GetInt32(2);
            }
            tx.Commit();
        }
        var state = "sent";
        try
        {
            // Final check before network I/O. An already accepted SMTP message cannot be recalled.
            using var valid = Command(db, null, """
                SELECT COUNT(*) FROM EmailOutbox o JOIN EmailPreferences p ON p.UserId=o.UserId AND p.Revision=o.Revision
                WHERE o.Id=$id AND ((o.Kind='weekly' AND p.Enabled=1 AND p.Verified=1)
                    OR (o.Kind='verification' AND p.Verified=0 AND p.TokenHash IS NOT NULL AND p.TokenExpires>$now))
                """, ("$id", id), ("$now", Now));
            if (Convert.ToInt32(await valid.ExecuteScalarAsync()) == 0) state = "cancelled";
            else
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(45));
                await transport.Send(preferences.Decode(payload), id, timeout.Token);
            }
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException)
        {
            state = "failed";
            logger.LogWarning("Email {Id} has an unreadable protected payload; marked failed", id);
        }
        catch (Exception)
        {
            state = attempt >= 6 ? "failed" : "pending";
            // SMTP exceptions may include recipient addresses or server details: do not log them.
            logger.LogWarning("Email {Id} attempt {Attempt} did not complete; state {State}", id, attempt, state);
        }
        using var finish = Command(db, null, """
            UPDATE EmailOutbox SET State=$state,Payload=CASE WHEN $state='pending' THEN Payload ELSE '' END,
                LeaseUntil=NULL,NextAttempt=$next,SentAt=CASE WHEN $state='sent' THEN $now ELSE NULL END
            WHERE Id=$id AND State='sending' AND Attempts=$attempt AND LeaseUntil=$lease
            """, ("$state", state), ("$next", Now + Math.Min(21600, 60 * (1L << Math.Min(attempt, 8)))),
            ("$now", Now), ("$id", id), ("$attempt", attempt), ("$lease", lease));
        await finish.ExecuteNonQueryAsync();
        return true;
    }
}

public sealed class EmailWorker(EmailQueue queue, ILogger<EmailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await queue.Schedule(stoppingToken);
                for (var i = 0; i < 50 && !stoppingToken.IsCancellationRequested; i++)
                    if (!await queue.ProcessOne(stoppingToken)) break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogError("Weekly email worker failed; will retry on the next poll"); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
