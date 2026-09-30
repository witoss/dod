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
  expect(balanceDescription(-100)).toBe("-100 kcal · burned more than eaten");
  expect(balanceDescription(100)).toBe("+100 kcal · burned less than eaten");
  expect(balanceDescription(0)).toBe("0 kcal · eaten equals burned");
});

test("cumulative balance sums daily values in chronological order", async () => {
  const { cumulativeBalances } = await import("./balance");
  const days = [
    { date: "2026-09-28", caloriesBurned: 2500, balance: -300 },
    { date: "2026-09-26", caloriesBurned: 1800, balance: 400 },
    { date: "2026-09-27", caloriesBurned: 2200, balance: 0 },
  ];
  expect(cumulativeBalances(days).map((day) => day.cumulative)).toEqual([
    400, 400, 100,
  ]);
  expect(days[0].date).toBe("2026-09-28");
});

test("missing days stay gaps and do not reset the running sum", async () => {
  const { cumulativeBalances } = await import("./balance");
  const days = balanceDays(
    [
      { date: "2026-09-26", caloriesBurned: 2500 },
      { date: "2026-09-28", caloriesBurned: 2300 },
    ],
    2200,
    "2026-09-28",
    4,
  );
  expect(cumulativeBalances(days).map((day) => day.cumulative)).toEqual([
    null,
    -300,
    null,
    -400,
  ]);
  expect(
    cumulativeBalances(days.slice(2)).map((day) => day.cumulative),
  ).toEqual([null, -100]);
});

test("daily eaten override takes precedence, including zero, while null uses reference", () => {
  const entries = [
    { date: "2026-09-26", caloriesBurned: 2500, caloriesEaten: 2700 },
    { date: "2026-09-27", caloriesBurned: 2500, caloriesEaten: 0 },
    { date: "2026-09-28", caloriesBurned: 2500, caloriesEaten: null },
  ];
  expect(
    balanceDays(entries, 2200, "2026-09-28", 3).map((day) => day.balance),
  ).toEqual([200, -2500, -300]);
  expect(
    balanceDays(entries, 2400, "2026-09-28", 3).map((day) => day.balance),
  ).toEqual([200, -2500, -100]);
});

test("intake without a burn entry does not invent a balance", () => {
  expect(
    balanceDays(
      [{ date: "2026-09-28", caloriesBurned: null, caloriesEaten: 2100 }],
      2200,
      "2026-09-28",
      1,
    )[0].balance,
  ).toBeNull();
});

test("cumulative balance mixes reference days and overridden intake", async () => {
  const { cumulativeBalances } = await import("./balance");
  const days = balanceDays(
    [
      { date: "2026-09-26", caloriesBurned: 2500, caloriesEaten: 2700 },
      { date: "2026-09-27", caloriesBurned: 2500 },
      { date: "2026-09-28", caloriesBurned: 2400, caloriesEaten: 2300 },
    ],
    2200,
    "2026-09-28",
    3,
  );
  expect(cumulativeBalances(days).map((day) => day.cumulative)).toEqual([
    200, -100, -200,
  ]);
});
