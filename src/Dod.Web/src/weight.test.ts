import { expect, test } from "vitest";
import { kilogramsLost } from "./weight";

test("uses earliest and latest weight dates regardless of order or missing weights", () => {
  const entries = [
    { date: "2026-10-03", weightKg: null },
    { date: "2026-10-02", weightKg: 87.5 },
    { date: "2026-09-01", weightKg: 90 },
    { date: "2026-09-15", weightKg: 89 },
    { date: "2026-08-01", weightKg: null },
  ];
  const original = [...entries];
  expect(kilogramsLost(entries)).toBe(2.5);
  expect(entries).toEqual(original);
});

test("distinguishes no weights from one measurement", () => {
  expect(kilogramsLost([])).toBeNull();
  expect(kilogramsLost([{ date: "2026-10-01", weightKg: null }])).toBeNull();
  expect(kilogramsLost([{ date: "2026-10-01", weightKg: 90 }])).toBe(0);
});

test("reports gains as negative and preserves hundredths", () => {
  expect(kilogramsLost([
    { date: "2026-10-01", weightKg: 80.1 },
    { date: "2026-10-02", weightKg: 80.3 },
  ])).toBe(-0.2);
});
