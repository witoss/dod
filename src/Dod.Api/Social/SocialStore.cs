using Dod.Api.Entries;
using Microsoft.Data.Sqlite;

namespace Dod.Api.Social;

public record Person(string Id, string Nickname, bool HasAvatar);
public record Friend(Person User, string Status, bool Incoming);
public record Challenge(string Id, string OwnerId, string Name, string StartDate, string EndDate, string Status);
public record Standing(string Id, string Nickname, bool HasAvatar, decimal? KgLost, int Measurements, string? BaselineDate, string? LatestDate, int? Rank);
public sealed class SocialStore(EntryStore entries, TimeProvider clock)
{
    public string UserId => entries.UserId;
    public string Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).ToString("yyyy-MM-dd");
    internal async Task<List<T>> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] values)
    {
        await using var db = await entries.OpenAsync(); using var cmd = Command(db, sql, values);
        using var reader = await cmd.ExecuteReaderAsync(); var result = new List<T>();
        while (await reader.ReadAsync()) result.Add(map(reader)); return result;
    }
    internal async Task<int> Execute(string sql, params (string, object?)[] values)
    {
        await using var db = await entries.OpenAsync(); using var cmd = Command(db, sql, values); return await cmd.ExecuteNonQueryAsync();
    }
    private SqliteCommand Command(SqliteConnection db, string sql, (string, object?)[] values)
    {
        var cmd = db.CreateCommand(); cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$me", UserId); cmd.Parameters.AddWithValue("$today", Today);
        foreach (var (name, value) in values) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
    public Task<List<Friend>> Friends() => Query("""
        SELECT u.Id,u.Nickname,u.Avatar IS NOT NULL,f.Status,f.Recipient=$me
        FROM Friendships f JOIN Users u ON u.Id=CASE WHEN f.Sender=$me THEN f.Recipient ELSE f.Sender END
        WHERE f.Sender=$me OR f.Recipient=$me ORDER BY u.NormalizedNickname
        """, r => new Friend(new(r.GetString(0),r.GetString(1),r.GetBoolean(2)),r.GetString(3),r.GetBoolean(4)));
    public Task<List<Challenge>> Challenges() => Query("""
        SELECT c.Id,c.OwnerId,c.Name,c.StartDate,c.EndDate,p.Status FROM Challenges c
        JOIN Participants p ON p.ChallengeId=c.Id WHERE p.UserId=$me ORDER BY c.StartDate DESC,c.Id
        """, r => new Challenge(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5)));
    public async Task CreateChallenge(string id, string name, string start, string end)
    {
        await using var db = await entries.OpenAsync(); using var tx = db.BeginTransaction();
        using var cmd = Command(db, """
            INSERT INTO Challenges VALUES ($id,$me,$name,$start,$end);
            INSERT INTO Participants VALUES ($id,$me,$me,'accepted');
            """, [("$id",id),("$name",name),("$start",start),("$end",end)]);
        cmd.Transaction = tx; await cmd.ExecuteNonQueryAsync(); tx.Commit();
    }
    public async Task<List<Standing>> Leaderboard(string id)
    {
        var people = await Query("""
            SELECT u.Id,u.Nickname,u.Avatar IS NOT NULL FROM Participants p JOIN Users u ON u.Id=p.UserId
            WHERE p.ChallengeId=$id AND p.Status='accepted' ORDER BY u.NormalizedNickname
            """, r => new Person(r.GetString(0),r.GetString(1),r.GetBoolean(2)), ("$id",id));
        var data = await Query("""
            SELECT e.UserId,e.Date,e.WeightKg FROM Entries e
            JOIN Participants p ON p.UserId=e.UserId AND p.Status='accepted'
            JOIN Challenges c ON c.Id=p.ChallengeId
            WHERE c.Id=$id AND e.WeightKg IS NOT NULL AND e.Date>=c.StartDate AND e.Date<c.EndDate AND e.Date<=$today
            ORDER BY e.Date
            """, r => (User:r.GetString(0), Date:r.GetString(1), Weight:r.GetDecimal(2)), ("$id",id));
        var standings = people.Select(p =>
        {
            var weights = data.Where(e => e.User == p.Id).ToList();
            decimal? lost = weights.Count >= 2 ? Math.Round(weights[0].Weight - weights[^1].Weight, 2) : null;
            return new Standing(p.Id,p.Nickname,p.HasAvatar,lost,weights.Count,weights.FirstOrDefault().Date,weights.LastOrDefault().Date,null);
        }).OrderByDescending(s => s.KgLost.HasValue).ThenByDescending(s => s.KgLost).ThenBy(s => s.Nickname, StringComparer.OrdinalIgnoreCase).ToList();
        return standings.Select(s => s with { Rank = s.KgLost.HasValue ? 1 + standings.Count(other => other.KgLost > s.KgLost) : null }).ToList();
    }
}
