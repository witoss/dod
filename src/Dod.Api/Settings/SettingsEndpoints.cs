using Dod.Api.Entries;

namespace Dod.Api.Settings;

public record CalorieReferenceInput(int? Calories);
public record CalorieReferenceResponse(int? Calories);

public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/settings").RequireAuthorization();
        group.MapGet("/calorie-reference", async (EntryStore store) =>
            Results.Ok(new CalorieReferenceResponse(await store.GetCalorieReferenceAsync())));
        group.MapPut("/calorie-reference", async (CalorieReferenceInput input, EntryStore store) =>
        {
            if (input.Calories is null or <= 0 or > 100000)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["calories"] = ["Enter a whole number from 1 to 100000 kcal."]
                });
            await store.SaveCalorieReferenceAsync(input.Calories.Value);
            return Results.NoContent();
        });
    }
}
