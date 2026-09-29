import { expect, test } from "vitest";
import { balanceDays, balanceDescription } from "./balance";

test("calculates both sides of the reference and an exact match", () => {
  const days = balanceDays(
    [
      { date: "2026-09-28", caloriesBurned: 2500 },
      { date: "2026-09-26", caloriesBurned: 1800 },
      { date: "2026-09-27", caloriesBurned: 2200 },
    ],
    2200,
    "2026-09-28",
    3,
  );
  expect(days.map((day) => day.balance)).toEqual([400, 0, -300]);
});

test("missing days and weight-only days stay missing, but zero is measured", () => {
  const days = balanceDays(
    [
      { date: "2026-09-27", caloriesBurned: null },
      { date: "2026-09-28", caloriesBurned: 0 },
    ],
    2200,
    "2026-09-28",
    3,
  );
  expect(days.map((day) => day.balance)).toEqual([null, null, 2200]);
});

test("builds a consecutive calendar window across a month boundary", () => {
  expect(balanceDays([], 2200, "2026-03-02", 4).map((day) => day.date)).toEqual(
    ["2026-02-27", "2026-02-28", "2026-03-01", "2026-03-02"],
  );
});

test("a changed reference recalculates history without changing measurements", () => {
  const entries = [{ date: "2026-09-28", caloriesBurned: 2400 }];
  expect(balanceDays(entries, 2200, "2026-09-28", 1)[0].balance).toBe(-200);
  expect(balanceDays(entries, 2600, "2026-09-28", 1)[0].balance).toBe(200);
  expect(entries[0].caloriesBurned).toBe(2400);
});

test("excludes data outside the selected period", () => {
  const days = balanceDays(
    [
      { date: "2026-09-25", caloriesBurned: 1000 },
      { date: "2026-09-29", caloriesBurned: 2000 },
    ],
    2200,
    "2026-09-28",
    3,
  );
  expect(days.every((day) => day.balance === null)).toBe(true);
});

test("labels the sign as a burn comparison", () => {
  expect(balanceDescription(-100)).toBe("-100 kcal · burned above reference");
  expect(balanceDescription(100)).toBe("+100 kcal · burned below reference");
  expect(balanceDescription(0)).toBe("0 kcal · on reference");
});
