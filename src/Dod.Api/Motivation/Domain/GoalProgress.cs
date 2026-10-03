namespace Dod.Api.Motivation.Domain;

// Pure domain rules: no HTTP, database, clock or email dependencies.
public static class GoalProgress
{
    public const int WeeklyBonusXp = 50;

    public static bool CanReport(string kind, DateOnly date, DateOnly today, DateOnly availableFrom) =>
        kind == "manual" && date <= today && date >= availableFrom;

    public static bool IsClosedEligibleWeek(DateOnly monday, DateOnly today, DateOnly eligibleFrom) =>
        monday >= eligibleFrom && monday.AddDays(7) <= today;

    public static int WeeklyBonus(int rosterCount, int completedReports) =>
        rosterCount > 0 && completedReports == (long)rosterCount * 7 ? WeeklyBonusXp : 0;
}
