using System.Globalization;
using Microsoft.Data.Sqlite;

using Dod.Api.Motivation.Domain;

namespace Dod.Api.Motivation.Persistence;

public static class WeeklyRules
{
    public static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static DateOnly Monday(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
    public static DateOnly NextMonday(DateOnly date) => Monday(date).AddDays(7);
    public static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return cmd;
    }
    public static async Task<long> Total(SqliteConnection db, SqliteTransaction? tx, string user)
    {
        using var cmd = Command(db, tx, """
            SELECT (SELECT COALESCE(SUM(Completed*Points),0) FROM ActivityReports WHERE UserId=$user)
                + (SELECT COALESCE(SUM(Points),0) FROM WeeklyAwards WHERE UserId=$user)
            """, ("$user", user));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
    // A week's roster is fixed by changes effective at its Monday; later admin edits cannot change it.
    public static async Task<List<(string Id, string Name)>> Roster(SqliteConnection db, SqliteTransaction? tx, DateOnly monday)
    {
        using var cmd = Command(db, tx, """
            SELECT a.ActivityId,a.Name FROM WeeklyActivitySchedule a
            WHERE a.EffectiveFrom=(SELECT MAX(b.EffectiveFrom) FROM WeeklyActivitySchedule b
                WHERE b.ActivityId=a.ActivityId AND b.EffectiveFrom<=$week) AND a.IsActive=1
            ORDER BY a.Name,a.ActivityId
            """, ("$week", Day(monday)));
        using var rows = await cmd.ExecuteReaderAsync(); var result = new List<(string, string)>();
        while (await rows.ReadAsync()) result.Add((rows.GetString(0), rows.GetString(1)));
        return result;
    }
    public static async Task Reconcile(SqliteConnection db, SqliteTransaction tx, string user, DateOnly date, DateOnly today)
    {
        var monday = Monday(date);
        using var eligible = Command(db, tx, "SELECT WeeklyEligibleFrom FROM Users WHERE Id=$user", ("$user", user));
        var from = (string?)await eligible.ExecuteScalarAsync();
        if (from is null || !GoalProgress.IsClosedEligibleWeek(monday, today, DateOnly.ParseExact(from, "yyyy-MM-dd"))) return;
        var roster = await Roster(db, tx, monday);
        using var count = Command(db, tx, """
            SELECT COUNT(*) FROM ActivityReports r WHERE r.UserId=$user AND r.Date>=$start AND r.Date<$end AND r.Completed=1
            AND EXISTS (SELECT 1 FROM WeeklyActivitySchedule a WHERE a.ActivityId=r.ActivityId AND a.IsActive=1
                AND a.EffectiveFrom=(SELECT MAX(b.EffectiveFrom) FROM WeeklyActivitySchedule b WHERE b.ActivityId=a.ActivityId AND b.EffectiveFrom<=$start))
            """, ("$user", user), ("$start", Day(monday)), ("$end", Day(monday.AddDays(7))));
        var points = GoalProgress.WeeklyBonus(roster.Count, Convert.ToInt32(await count.ExecuteScalarAsync()));
        using var save = Command(db, tx, """
            INSERT INTO WeeklyAwards(UserId,WeekStart,Points) VALUES($user,$week,$points)
            ON CONFLICT(UserId,WeekStart) DO UPDATE SET Points=excluded.Points
            """, ("$user", user), ("$week", Day(monday)), ("$points", points));
        await save.ExecuteNonQueryAsync();
    }
    public static async Task<int> Bonus(SqliteConnection db, SqliteTransaction tx, string user, DateOnly date)
    {
        using var cmd = Command(db, tx, "SELECT Points FROM WeeklyAwards WHERE UserId=$user AND WeekStart=$week",
            ("$user", user), ("$week", Day(Monday(date))));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }
}
