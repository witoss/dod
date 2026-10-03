using System.Globalization;
using Dod.Api.Accounts;

namespace Dod.Api.Motivation;

public static class MotivationEndpoints
{
    public static void MapMotivationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/motivation").RequireAuthorization();
        group.MapGet("/experience", async (MotivationStore store) => Results.Ok(await store.GetExperience()));
        group.MapGet("/{date}", async (DateOnly date, MotivationStore store) => Results.Ok(await store.GetDay(date)));
        group.MapPut("/{date}/activities/{id}", async (DateOnly date, string id, ReportInput input, MotivationStore store, HttpContext context) =>
        {
            var result = await store.Report(date, id, input.Completed);
            if (result is null) return AccountEndpoints.Error("This activity cannot be reported for that date. Only available manual activities on or before today (UTC) can be reported.");
            WriteXpHeaders(context, result);
            return Results.Ok(result);
        });
        var admin = app.MapGroup("/api/admin").RequireAuthorization();
        admin.AddEndpointFilter(async (context, next) =>
        {
            var store = context.HttpContext.RequestServices.GetRequiredService<MotivationStore>();
            return await store.IsAdmin() ? await next(context) : Results.Forbid();
        });
        admin.MapGet("/users", async (MotivationStore store) => Results.Ok(await store.Users()));
        admin.MapGet("/challenges", async (MotivationStore store) => Results.Ok(await store.Challenges()));
        admin.MapGet("/activities", async (MotivationStore store) => Results.Ok(await store.Definitions()));
        admin.MapPost("/activities", async (ActivityInput input, MotivationStore store) =>
        {
            if (!Valid(input)) return InvalidActivity();
            await store.SaveDefinition(null, input);
            return Results.NoContent();
        });
        admin.MapPut("/activities/{id}", async (string id, ActivityInput input, MotivationStore store) =>
        {
            if (!Valid(input)) return InvalidActivity();
            return await store.SaveDefinition(id, input) ? Results.NoContent() : Results.NotFound();
        });
    }
    public static void WriteXpHeaders(HttpContext context, XpChange change)
    {
        context.Response.Headers["X-XP-Change"] = change.Awarded.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["X-XP-Total"] = change.Experience.TotalXp.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["X-XP-Level-Up"] = change.LeveledUp ? "true" : "false";
    }
    private static bool Valid(ActivityInput input) => !string.IsNullOrWhiteSpace(input.Name) && input.Name.Trim().Length <= 80
        && !string.IsNullOrWhiteSpace(input.Description) && input.Description.Trim().Length <= 300
        && input.Points is >= 1 and <= 1000
        && (string.IsNullOrEmpty(input.CutoffTime) || TimeOnly.TryParseExact(input.CutoffTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
    private static IResult InvalidActivity() => AccountEndpoints.Error("Use a name up to 80 characters, a description up to 300 characters, 1–1000 points, and an optional cutoff in HH:mm format.");
}
