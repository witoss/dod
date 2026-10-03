using Dod.Api.Accounts;
using Dod.Api.Entries;

namespace Dod.Api.Notifications;

public static class EmailEndpoints
{
    public static void MapEmailEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/email").RequireRateLimiting("accounts");
        group.MapGet("/preferences", async (EmailPreferencesStore store, EntryStore entries) => Results.Ok(await store.Get(entries.UserId))).RequireAuthorization();
        group.MapPut("/preferences", async (EmailInput input, EmailPreferencesStore store, EntryStore entries) =>
        {
            var error = await store.Save(entries.UserId, input);
            return error is null ? Results.Ok(await store.Get(entries.UserId)) : AccountEndpoints.Error(error);
        }).RequireAuthorization();
        group.MapPost("/resend", async (EmailPreferencesStore store, EntryStore entries) =>
        {
            var prefs = await store.Get(entries.UserId);
            var error = await store.Save(entries.UserId, new(prefs.Email, prefs.Enabled), true);
            return error is null ? Results.NoContent() : AccountEndpoints.Error(error);
        }).RequireAuthorization();
        group.MapPost("/verify", async (TokenInput input, EmailPreferencesStore store) =>
            await store.Verify(input.Token) ? Results.NoContent() : AccountEndpoints.Error("This confirmation link is invalid, expired, or already used. Request a new one in your profile."));
        group.MapPost("/unsubscribe", async (TokenInput input, EmailPreferencesStore store) =>
            await store.Unsubscribe(input.Token) ? Results.NoContent() : AccountEndpoints.Error("This unsubscribe link is invalid or belongs to an older email address."));
        app.MapGet("/api/motivation/weekly/{date}", async (DateOnly date, WeeklySummaryStore store, EntryStore entries) =>
            Results.Ok(await store.Build(entries.UserId, date))).RequireAuthorization();
    }
}
