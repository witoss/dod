import { localDate } from "../date";

export type CalorieMeasurement = {
  date: string;
  caloriesBurned: number | null;
  caloriesEaten?: number | null;
};
export type BalanceDay = CalorieMeasurement & { balance: number | null };

export function cumulativeBalances(days: BalanceDay[]) {
  let total = 0;
  return [...days]
    .sort((a, b) => a.date.localeCompare(b.date))
    .map((day) => {
      if (day.balance === null) return { ...day, cumulative: null };
      total += day.balance;
      return { ...day, cumulative: total };
    });
}

export function balanceDays(
  entries: CalorieMeasurement[],
  reference: number,
  through: string,
  count: number,
): BalanceDay[] {
  const measurements = new Map(entries.map((entry) => [entry.date, entry]));
  const date = new Date(`${through}T12:00:00`);
  date.setDate(date.getDate() - count + 1);
  return Array.from({ length: count }, () => {
    const key = localDate(date);
    const entry = measurements.get(key);
    const caloriesBurned = entry?.caloriesBurned ?? null;
    const caloriesEaten = entry?.caloriesEaten ?? null;
    date.setDate(date.getDate() + 1);
    return {
      date: key,
      caloriesBurned,
      caloriesEaten,
      balance:
        caloriesBurned === null
          ? null
          : (caloriesEaten ?? reference) - caloriesBurned,
    };
  });
}

export function signedCalories(value: number): string {
  return `${value > 0 ? "+" : ""}${value.toLocaleString()}`;
}

export function balanceDescription(value: number): string {
  if (value === 0) return "0 kcal · eaten equals burned";
  return `${signedCalories(value)} kcal · burned ${value < 0 ? "more than" : "less than"} eaten`;
}
