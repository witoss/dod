using System.Globalization;
using Dod.Api.Entries;
using Microsoft.Data.Sqlite;

namespace Dod.Api.Motivation;

public record Experience(long TotalXp, long Level, long XpIntoLevel, int XpPerLevel, long XpToNextLevel)
{
    public static Experience From(long xp) => new(xp, xp / 100 + 1, xp % 100, 100, 100 - xp % 100);
}
public record XpChange(long Awarded, Experience Experience, bool LeveledUp);
public record ActivityDefinition(string Id, string Name, string Description, string Kind, int Points, string? CutoffTime, bool IsActive, string AvailableFrom);
public record DailyActivity(string Id, string Name, string Description, string Kind, int Points, string? CutoffTime, bool IsActive, bool? Completed, int Earned, bool CanReport);
public record MotivationDay(string Date, string Today, Experience Experience, List<DailyActivity> Activities);
public record ActivityInput(string Name, string Description, int Points, string? CutoffTime, bool IsActive);
public record ReportInput([property: System.Text.Json.Serialization.JsonRequired] bool Completed);

public sealed class MotivationStore(EntryStore entries, TimeProvider clock)
{
    public string Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? transaction, string sql, params (string Key, object? Value)[] values)
    {
        var cmd = db.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        foreach (var (key, value) in values) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return cmd;
    }
    private async Task<long> Total(SqliteConnection db, SqliteTransaction? transaction)
    {
        using var cmd = Command(db, transaction, "SELECT COALESCE(SUM(Completed*Points),0) FROM ActivityReports WHERE UserId=$user", ("$user", entries.UserId));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
    public async Task<Experience> GetExperience()
    {
        await using var db = await entries.OpenAsync();
        return Experience.From(await Total(db, null));
    }
    public async Task<MotivationDay> GetDay(DateOnly date)
    {
        var day = Format(date);
        await using var db = await entries.OpenAsync();
        using var tx = db.BeginTransaction();
        using var cmd = Command(db, tx, """
            SELECT a.Id,COALESCE(r.Name,a.Name),COALESCE(r.Description,a.Description),a.Kind,
                COALESCE(r.Points,a.Points),CASE WHEN r.UserId IS NULL THEN a.CutoffTime ELSE r.CutoffTime END,
                a.IsActive,r.Completed,COALESCE(r.Completed*r.Points,0),a.AvailableFrom
            FROM Activities a LEFT JOIN ActivityReports r ON r.ActivityId=a.Id AND r.UserId=$user AND r.Date=$date
            WHERE (a.IsActive=1 AND a.AvailableFrom<=$date) OR r.UserId IS NOT NULL
            ORDER BY a.Kind DESC,a.Name,a.Id
            """, ("$user", entries.UserId), ("$date", day));
        var activities = new List<DailyActivity>();
        using (var rows = await cmd.ExecuteReaderAsync())
        {
            while (await rows.ReadAsync())
                activities.Add(new(rows.GetString(0), rows.GetString(1), rows.GetString(2), rows.GetString(3), rows.GetInt32(4),
                    rows.IsDBNull(5) ? null : rows.GetString(5), rows.GetBoolean(6), rows.IsDBNull(7) ? null : rows.GetBoolean(7), rows.GetInt32(8),
                    rows.GetString(3) == "manual" && string.CompareOrdinal(day, Today) <= 0 && string.CompareOrdinal(day, rows.GetString(9)) >= 0));
        }
        var experience = Experience.From(await Total(db, tx));
        tx.Commit();
        return new(day, Today, experience, activities);
    }
    public async Task<XpChange?> Report(DateOnly date, string activityId, bool completed)
    {
        var day = Format(date);
        if (string.CompareOrdinal(day, Today) > 0) return null;
        await using var db = await entries.OpenAsync();
        using var tx = db.BeginTransaction();
        var before = await Total(db, tx);
        using var cmd = Command(db, tx, """
            INSERT INTO ActivityReports (UserId,ActivityId,Date,Completed,Points,Name,Description,CutoffTime)
            SELECT $user,Id,$date,$completed,Points,Name,Description,CutoffTime FROM Activities a
            WHERE Id=$id AND Kind='manual' AND AvailableFrom<=$date
                AND (IsActive=1 OR EXISTS (SELECT 1 FROM ActivityReports r WHERE r.UserId=$user AND r.ActivityId=a.Id AND r.Date=$date))
            ON CONFLICT(UserId,ActivityId,Date) DO UPDATE SET Completed=excluded.Completed
            """, ("$user", entries.UserId), ("$date", day), ("$id", activityId), ("$completed", completed ? 1 : 0));
        if (await cmd.ExecuteNonQueryAsync() == 0) return null;
        var after = await Total(db, tx);
        tx.Commit();
        return new(after - before, Experience.From(after), after / 100 > before / 100);
    }
    public async Task<XpChange> RecordWeight(DateOnly date, decimal weight)
    {
        var day = Format(date);
        await using var db = await entries.OpenAsync();
        using var tx = db.BeginTransaction();
        var before = await Total(db, tx);
        using var cmd = Command(db, tx, """
            INSERT INTO Entries (UserId,Date,WeightKg) VALUES ($user,$date,$weight)
            ON CONFLICT(UserId,Date) DO UPDATE SET WeightKg=excluded.WeightKg;
            INSERT INTO ActivityReports (UserId,ActivityId,Date,Completed,Points,Name,Description,CutoffTime)
            SELECT $user,Id,$date,1,CASE WHEN IsActive=1 AND AvailableFrom<=$date THEN Points ELSE 0 END,Name,Description,NULL
            FROM Activities WHERE Kind='weight' AND $date<=$today
            ON CONFLICT(UserId,ActivityId,Date) DO NOTHING;
            """, ("$user", entries.UserId), ("$date", day), ("$weight", weight), ("$today", Today));
        await cmd.ExecuteNonQueryAsync();
        var after = await Total(db, tx);
        tx.Commit();
        return new(after - before, Experience.From(after), after / 100 > before / 100);
    }
    public async Task<bool> IsAdmin()
    {
        await using var db = await entries.OpenAsync();
        using var cmd = Command(db, null, "SELECT IsAdmin FROM Users WHERE Id=$user AND PasswordHash IS NOT NULL", ("$user", entries.UserId));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 1;
    }
    public async Task<List<ActivityDefinition>> Definitions()
    {
        await using var db = await entries.OpenAsync();
        using var cmd = Command(db, null, "SELECT Id,Name,Description,Kind,Points,CutoffTime,IsActive,AvailableFrom FROM Activities ORDER BY Kind DESC,Name,Id");
        using var rows = await cmd.ExecuteReaderAsync();
        var result = new List<ActivityDefinition>();
        while (await rows.ReadAsync()) result.Add(new(rows.GetString(0), rows.GetString(1), rows.GetString(2), rows.GetString(3), rows.GetInt32(4), rows.IsDBNull(5) ? null : rows.GetString(5), rows.GetBoolean(6), rows.GetString(7)));
        return result;
    }
    public async Task<bool> SaveDefinition(string? id, ActivityInput input)
    {
        await using var db = await entries.OpenAsync();
        using var cmd = Command(db, null, id is null ? """
            INSERT INTO Activities VALUES ($id,$name,$description,'manual',$points,$cutoff,$active,$today)
            """ : """
            UPDATE Activities SET Name=$name,Description=$description,Points=$points,
                CutoffTime=CASE WHEN Kind='weight' THEN NULL ELSE $cutoff END,IsActive=$active WHERE Id=$id
            """, ("$id", id ?? Guid.NewGuid().ToString("N")), ("$name", input.Name.Trim()), ("$description", input.Description.Trim()),
            ("$points", input.Points), ("$cutoff", string.IsNullOrEmpty(input.CutoffTime) ? null : input.CutoffTime), ("$active", input.IsActive ? 1 : 0), ("$today", Today));
        return await cmd.ExecuteNonQueryAsync() == 1;
    }
    public async Task<List<AdminUser>> Users()
    {
        await using var db = await entries.OpenAsync();
        using var cmd = Command(db, null, """
            SELECT u.Id,u.Nickname,u.IsAdmin,COALESCE(SUM(r.Completed*r.Points),0)
            FROM Users u LEFT JOIN ActivityReports r ON r.UserId=u.Id
            WHERE u.PasswordHash IS NOT NULL GROUP BY u.Id ORDER BY u.NormalizedNickname
            """);
        using var rows = await cmd.ExecuteReaderAsync();
        var result = new List<AdminUser>();
        while (await rows.ReadAsync()) result.Add(new(rows.GetString(0), rows.GetString(1), rows.GetBoolean(2), Experience.From(rows.GetInt64(3))));
        return result;
    }
    public async Task<List<AdminChallenge>> Challenges()
    {
        await using var db = await entries.OpenAsync();
        using var cmd = Command(db, null, """
            SELECT c.Id,c.Name,u.Nickname,c.StartDate,c.EndDate,
                COALESCE(SUM(CASE WHEN p.Status='accepted' THEN 1 ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN p.Status='pending' THEN 1 ELSE 0 END),0)
            FROM Challenges c JOIN Users u ON u.Id=c.OwnerId LEFT JOIN Participants p ON p.ChallengeId=c.Id
            GROUP BY c.Id ORDER BY c.StartDate DESC,c.Id
            """);
        using var rows = await cmd.ExecuteReaderAsync();
        var result = new List<AdminChallenge>();
        while (await rows.ReadAsync()) result.Add(new(rows.GetString(0), rows.GetString(1), rows.GetString(2), rows.GetString(3), rows.GetString(4), rows.GetInt32(5), rows.GetInt32(6)));
        return result;
    }
}
public record AdminUser(string Id, string Nickname, bool IsAdmin, Experience Experience);
public record AdminChallenge(string Id, string Name, string Owner, string StartDate, string EndDate, int Participants, int PendingInvitations);
