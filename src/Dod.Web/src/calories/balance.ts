import { localDate } from "../date";

export type CalorieMeasurement = {
  date: string;
  caloriesBurned: number | null;
};
export type BalanceDay = CalorieMeasurement & { balance: number | null };

export function balanceDays(
  entries: CalorieMeasurement[],
  reference: number,
  through: string,
  count: number,
): BalanceDay[] {
  const measurements = new Map(
    entries.map((entry) => [entry.date, entry.caloriesBurned]),
  );
  const date = new Date(`${through}T12:00:00`);
  date.setDate(date.getDate() - count + 1);
  return Array.from({ length: count }, () => {
    const key = localDate(date);
    const caloriesBurned = measurements.get(key) ?? null;
    date.setDate(date.getDate() + 1);
    return {
      date: key,
      caloriesBurned,
      balance: caloriesBurned === null ? null : reference - caloriesBurned,
    };
  });
}

export function signedCalories(value: number): string {
  return `${value > 0 ? "+" : ""}${value.toLocaleString()}`;
}

export function balanceDescription(value: number): string {
  if (value === 0) return "0 kcal · on reference";
  return `${signedCalories(value)} kcal · burned ${value < 0 ? "above" : "below"} reference`;
}
