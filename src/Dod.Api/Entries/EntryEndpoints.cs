using Dod.Api.Motivation;

namespace Dod.Api.Entries;

public record DailyEntry(DateOnly Date, decimal? WeightKg, int? CaloriesBurned)
{
    public int? CaloriesEaten { get; init; }
}
public record WeightInput(decimal WeightKg);
public record CaloriesInput(int CaloriesBurned);
public record EatenInput([property: System.Text.Json.Serialization.JsonRequired] int? CaloriesEaten);

public static class EntryEndpoints
{
    public static void MapEntryEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/entries").RequireAuthorization();
        group.MapGet("/", async (EntryStore store) => Results.Ok(await store.ListAsync()));
        group.MapPut("/{date}/eaten", async (DateOnly date, EatenInput input, EntryStore store) =>
        {
            if (input.CaloriesEaten is < 0 or > 100000)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["caloriesEaten"] = ["Enter a whole number from 0 to 100000 kcal, or null to use the reference."] });
            await store.SaveEatenAsync(date, input.CaloriesEaten);
            return Results.NoContent();
        });
        group.MapPut("/{date}/weight", async (DateOnly date, WeightInput input, MotivationStore store, HttpContext context) =>
        {
            if (input.WeightKg is <= 0 or > 1000)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["weightKg"] = ["Enter a weight greater than 0 and no more than 1000 kg."] });
            var change = await store.RecordWeight(date, input.WeightKg);
            MotivationEndpoints.WriteXpHeaders(context, change);
            return Results.NoContent();
        });
        group.MapPut("/{date}/calories", async (DateOnly date, CaloriesInput input, EntryStore store) =>
        {
            if (input.CaloriesBurned is < 0 or > 100000)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["caloriesBurned"] = ["Enter a whole number from 0 to 100000 kcal."] });
            await store.SaveCaloriesAsync(date, input.CaloriesBurned);
            return Results.NoContent();
        });
    }
}
