using Dod.Api.Entries;
using Dod.Api.Motivation;
using static Dod.Api.Notifications.WeeklyRules;

namespace Dod.Api.Notifications;

public record WeekCell(string Date, bool? Completed, int Xp);
public record WeekActivity(string Id, string Name, List<WeekCell> Days);
public record WeekChallenge(string Name, string Status, int? Rank, int Participants, decimal? KgLost);
public record WeeklySummary(string WeekStart, string WeekEnd, string EligibleFrom, bool Closed, int BonusXp,
    long ActivityXp, Experience Experience, decimal? KgLost, int WeightMeasurements,
    List<WeekActivity> Activities, List<WeekChallenge> Challenges);

public sealed class WeeklySummaryStore(EntryStore entries, TimeProvider clock)
{
    public async Task<WeeklySummary> Build(string user, DateOnly week)
    {
        week = Monday(week); var end = week.AddDays(7);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        await using var db = await entries.OpenAsync(); using var tx = db.BeginTransaction();
        await Reconcile(db, tx, user, week, today);
        using var prefs = Command(db, tx, "SELECT WeeklyEligibleFrom FROM Users WHERE Id=$user", ("$user", user));
        var eligible = (string)(await prefs.ExecuteScalarAsync())!;
        var roster = await Roster(db, tx, week);
        var reports = new Dictionary<(string, string), (bool Completed, int Xp)>();
        using (var cmd = Command(db, tx, "SELECT ActivityId,Date,Completed,Completed*Points FROM ActivityReports WHERE UserId=$user AND Date>=$start AND Date<$end",
            ("$user", user), ("$start", Day(week)), ("$end", Day(end))))
        using (var rows = await cmd.ExecuteReaderAsync())
            while (await rows.ReadAsync()) reports[(rows.GetString(0), rows.GetString(1))] = (rows.GetBoolean(2), rows.GetInt32(3));
        var activities = roster.Select(a => new WeekActivity(a.Id, a.Name, Enumerable.Range(0, 7).Select(i =>
        {
            var day = Day(week.AddDays(i));
            return reports.TryGetValue((a.Id, day), out var r) ? new WeekCell(day, r.Completed, r.Xp) : new WeekCell(day, null, 0);
        }).ToList())).ToList();
        var weights = new List<decimal>();
        using (var cmd = Command(db, tx, "SELECT WeightKg FROM Entries WHERE UserId=$user AND Date>=$start AND Date<$end AND Date<=$today AND WeightKg IS NOT NULL ORDER BY Date",
            ("$user", user), ("$start", Day(week)), ("$end", Day(end)), ("$today", Day(today))))
        using (var rows = await cmd.ExecuteReaderAsync()) while (await rows.ReadAsync()) weights.Add(rows.GetDecimal(0));
        using var bonusCmd = Command(db, tx, "SELECT Points FROM WeeklyAwards WHERE UserId=$user AND WeekStart=$week", ("$user", user), ("$week", Day(week)));
        var bonus = Convert.ToInt32(await bonusCmd.ExecuteScalarAsync());
        var challenges = new List<WeekChallenge>();
        // Restrict to the recipient's memberships; rankings use accepted members and measurements through Sunday.
        using (var cmd = Command(db, tx, """
            WITH scores AS (
                SELECT c.Id,p.UserId,
                    (SELECT COUNT(*) FROM Entries e WHERE e.UserId=p.UserId AND e.WeightKg IS NOT NULL
                        AND e.Date>=c.StartDate AND e.Date<c.EndDate AND e.Date<$end AND e.Date<=$today) AS N,
                    (SELECT e.WeightKg FROM Entries e WHERE e.UserId=p.UserId AND e.WeightKg IS NOT NULL
                        AND e.Date>=c.StartDate AND e.Date<c.EndDate AND e.Date<$end AND e.Date<=$today ORDER BY e.Date LIMIT 1)
                    - (SELECT e.WeightKg FROM Entries e WHERE e.UserId=p.UserId AND e.WeightKg IS NOT NULL
                        AND e.Date>=c.StartDate AND e.Date<c.EndDate AND e.Date<$end AND e.Date<=$today ORDER BY e.Date DESC LIMIT 1) AS Lost
                FROM Challenges c JOIN Participants p ON p.ChallengeId=c.Id AND p.Status='accepted'
                WHERE EXISTS (SELECT 1 FROM Participants me WHERE me.ChallengeId=c.Id AND me.UserId=$user AND me.Status='accepted')
            )
            SELECT c.Name,CASE WHEN me.Status='pending' THEN 'Invitation pending' WHEN c.StartDate>=$end THEN 'Upcoming'
                WHEN c.EndDate<=$end THEN 'Finished' ELSE 'Active' END,
                CASE WHEN s.N>=2 THEN 1+(SELECT COUNT(*) FROM scores other WHERE other.Id=c.Id AND other.N>=2 AND ROUND(other.Lost,2)>ROUND(s.Lost,2)) END,
                (SELECT COUNT(*) FROM scores other WHERE other.Id=c.Id),CASE WHEN s.N>=2 THEN ROUND(s.Lost,2) END
            FROM Challenges c JOIN Participants me ON me.ChallengeId=c.Id AND me.UserId=$user
            LEFT JOIN scores s ON s.Id=c.Id AND s.UserId=$user
            WHERE c.EndDate>$start ORDER BY c.StartDate,c.Name
            """, ("$user", user), ("$start", Day(week)), ("$end", Day(end)), ("$today", Day(today))))
        using (var rows = await cmd.ExecuteReaderAsync())
            while (await rows.ReadAsync()) challenges.Add(new(rows.GetString(0), rows.GetString(1), rows.IsDBNull(2) ? null : rows.GetInt32(2), rows.GetInt32(3), rows.IsDBNull(4) ? null : rows.GetDecimal(4)));
        var xp = await Total(db, tx, user); tx.Commit();
        return new(Day(week), Day(end.AddDays(-1)), eligible, end <= today, bonus, reports.Values.Sum(r => (long)r.Xp), Experience.From(xp),
            weights.Count >= 2 ? Math.Round(weights[0] - weights[^1], 2) : null, weights.Count, activities, challenges);
    }
}
