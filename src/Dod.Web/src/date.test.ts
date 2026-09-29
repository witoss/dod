import { expect, test } from "vitest";
import { localDate } from "./date";
test("uses the calendar date at local midnight, without UTC conversion", () => {
  expect(localDate(new Date(2026, 8, 28, 0, 1))).toBe("2026-09-28");
});
