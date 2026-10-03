namespace Dod.Api.Motivation;

public record Experience(long TotalXp, long Level, long XpIntoLevel, int XpPerLevel, long XpToNextLevel)
{
    public static Experience From(long xp) => new(xp, xp / 100 + 1, xp % 100, 100, 100 - xp % 100);
}
public record XpChange(long Awarded, Experience Experience, bool LeveledUp)
{
    public int WeeklyBonusChange { get; init; }
    public long ActivityChange => Awarded - WeeklyBonusChange;

    public static XpChange Between(long before, long after, int bonusBefore, int bonusAfter) =>
        new(after - before, Experience.From(after), after / 100 > before / 100)
        { WeeklyBonusChange = bonusAfter - bonusBefore };
}
