using Dod.Api.Accounts;
using Microsoft.Data.Sqlite;

namespace Dod.Api.Social;

public record FriendInput(string Nickname);
public record Decision(bool Accept);
public record ChallengeInput(string Name, DateOnly StartDate, int Weeks);
public record InviteInput(string UserId);
public static class SocialEndpoints
{
    public static void MapSocialEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/social").RequireAuthorization();
        group.MapGet("/friends", async (SocialStore store) => Results.Ok(await store.Friends()));
        group.MapGet("/users", async (string nickname, SocialStore store) =>
        {
            if (!AccountEndpoints.ValidNickname(nickname)) return Results.NotFound();
            var people = await store.Query("SELECT Id,Nickname,Avatar IS NOT NULL FROM Users WHERE NormalizedNickname=$nick AND PasswordHash IS NOT NULL AND Id<>$me",
                r => new Person(r.GetString(0),r.GetString(1),r.GetBoolean(2)), ("$nick",AccountEndpoints.Normalize(nickname)));
            return people.Count == 0 ? Results.NotFound() : Results.Ok(people[0]);
        }).RequireRateLimiting("accounts");
        group.MapPost("/friends", async (FriendInput input, SocialStore store) =>
        {
            if (!AccountEndpoints.ValidNickname(input.Nickname)) return AccountEndpoints.Error("Enter the full nickname.");
            try
            {
                var changed = await store.Execute("""
                    INSERT INTO Friendships SELECT $me,Id,'pending' FROM Users
                    WHERE NormalizedNickname=$nick AND Id<>$me AND PasswordHash IS NOT NULL
                    """, ("$nick",AccountEndpoints.Normalize(input.Nickname)));
                return changed == 1 ? Results.NoContent() : AccountEndpoints.Error("User not found.",404);
            }
            catch (SqliteException e) when (e.SqliteErrorCode == 19) { return AccountEndpoints.Error("A friendship or request already exists.",409); }
        });
        group.MapPut("/friends/{id}", async (string id, Decision input, SocialStore store) =>
        {
            var sql = input.Accept
                ? "UPDATE Friendships SET Status='accepted' WHERE Sender=$id AND Recipient=$me AND Status='pending'"
                : "DELETE FROM Friendships WHERE Sender=$id AND Recipient=$me AND Status='pending'";
            return await store.Execute(sql,("$id",id)) == 1 ? Results.NoContent() : Results.NotFound();
        });
        group.MapDelete("/friends/{id}", async (string id, SocialStore store) =>
        {
            await store.Execute("""
                DELETE FROM Friendships WHERE (Sender=$me AND Recipient=$id) OR (Sender=$id AND Recipient=$me);
                DELETE FROM Participants WHERE Status='pending' AND ((UserId=$me AND InvitedBy=$id) OR (UserId=$id AND InvitedBy=$me));
                """, ("$id",id));
            return Results.NoContent();
        });
        group.MapGet("/challenges", async (SocialStore store) => Results.Ok(await store.Challenges()));
        group.MapPost("/challenges", async (ChallengeInput input, SocialStore store) =>
        {
            var today = DateOnly.ParseExact(store.Today,"yyyy-MM-dd");
            if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 80 || input.Weeks is < 1 or > 52 || input.StartDate < today || input.StartDate > today.AddYears(1))
                return AccountEndpoints.Error("Use a name up to 80 characters, 1–52 weeks, and a start date from today through next year (UTC).");
            var id = Guid.NewGuid().ToString("N");
            await store.CreateChallenge(id,input.Name.Trim(),input.StartDate.ToString("yyyy-MM-dd"),input.StartDate.AddDays(input.Weeks*7).ToString("yyyy-MM-dd"));
            return Results.Ok(new { id });
        });
        group.MapPost("/challenges/{id}/invite", async (string id, InviteInput input, SocialStore store) =>
        {
            try
            {
                var count = await store.Execute("""
                    INSERT INTO Participants (ChallengeId,UserId,InvitedBy,Status)
                    SELECT c.Id,$friend,$me,'pending' FROM Challenges c
                    WHERE c.Id=$id AND c.StartDate>=$today
                    AND EXISTS (SELECT 1 FROM Participants p WHERE p.ChallengeId=c.Id AND p.UserId=$me AND p.Status='accepted')
                    AND EXISTS (SELECT 1 FROM Friendships f WHERE f.Status='accepted' AND
                        ((f.Sender=$me AND f.Recipient=$friend) OR (f.Sender=$friend AND f.Recipient=$me)))
                    """, ("$id",id),("$friend",input.UserId));
                return count == 1 ? Results.NoContent() : AccountEndpoints.Error("Only participants can invite their accepted friends, before or on the start date.",403);
            }
            catch (SqliteException e) when (e.SqliteErrorCode == 19) { return AccountEndpoints.Error("This person is already invited or participating.",409); }
        });
        group.MapPut("/challenges/{id}/invitation", async (string id, Decision input, SocialStore store) =>
        {
            var sql = input.Accept ? """
                UPDATE Participants SET Status='accepted' WHERE ChallengeId=$id AND UserId=$me AND Status='pending'
                AND EXISTS (SELECT 1 FROM Challenges c WHERE c.Id=$id AND c.StartDate>=$today)
                AND EXISTS (SELECT 1 FROM Friendships f WHERE f.Status='accepted' AND
                    ((f.Sender=$me AND f.Recipient=Participants.InvitedBy) OR (f.Recipient=$me AND f.Sender=Participants.InvitedBy)))
                """ : "DELETE FROM Participants WHERE ChallengeId=$id AND UserId=$me AND Status='pending'";
            return await store.Execute(sql,("$id",id)) == 1 ? Results.NoContent() : AccountEndpoints.Error("Invitation unavailable, expired, or friendship removed.",409);
        });
        group.MapDelete("/challenges/{id}/participation", async (string id, SocialStore store) =>
        {
            await store.Execute("""
                DELETE FROM Participants WHERE ChallengeId=$id AND UserId=$me;
                DELETE FROM Participants WHERE ChallengeId=$id AND InvitedBy=$me AND Status='pending';
                """, ("$id",id));
            return Results.NoContent();
        });
        group.MapGet("/challenges/{id}/leaderboard", async (string id, SocialStore store) =>
        {
            var allowed = await store.Query("SELECT 1 FROM Participants WHERE ChallengeId=$id AND UserId=$me AND Status='accepted'",r => r.GetInt32(0),("$id",id));
            return allowed.Count == 0 ? Results.NotFound() : Results.Ok(await store.Leaderboard(id));
        });
    }
}
