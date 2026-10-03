// @vitest-environment jsdom
import { useState } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { DailyActivities, ExperienceBar } from "./Motivation";
import { WeekGoals } from "./WeekGoals";
import { shiftDate } from "../date";
import { resetCsrf } from "../api";

function JournalGoals() {
  const [date, setDate] = useState("2026-09-21");
  return <><ExperienceBar /><WeekGoals date={date} onSelectDate={setDate} /><DailyActivities key={date} date={date} /></>;
}

describe("past-day goal editing", () => {
  let met: boolean;
  let failSave: boolean;
  let fetchMock: ReturnType<typeof vi.fn>;
  const experience = () => ({ totalXp: met ? 260 : 200, level: met ? 3 : 3, xpIntoLevel: met ? 60 : 0, xpPerLevel: 100, xpToNextLevel: met ? 40 : 100 });
  beforeEach(() => {
    met = false; failSave = false; resetCsrf();
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(new Date("2026-09-21T12:00:00Z"));
    fetchMock = vi.fn(async (url: string, init: RequestInit = {}) => {
      const json = (body: unknown, status = 200, headers = {}) => new Response(JSON.stringify(body), { status, headers });
      if (url === "/api/account/csrf") return json({ token: "csrf-token" });
      if (url === "/api/motivation/experience") return json(experience());
      if (url.startsWith("/api/motivation/weekly/")) {
        const weekStart = url.slice(-10);
        return json({ weekStart, weekEnd: shiftDate(weekStart, 6), eligibleFrom: "2026-09-14", closed: weekStart < "2026-09-21", bonusXp: met && weekStart === "2026-09-14" ? 50 : 0,
          activities: [{ id: "exercise", name: "Exercise", days: Array.from({ length: 7 }, (_, i) => ({ date: shiftDate(weekStart, i), completed: i === 0 ? met : true, xp: i === 0 && !met ? 0 : 10 })) }] });
      }
      if (init.method === "PUT") {
        if (failSave) return json({ detail: "Could not save this goal." }, 500);
        const completed = JSON.parse(init.body as string).completed;
        const delta = completed === met ? 0 : completed ? 60 : -60;
        met = completed;
        return json({}, 200, { "X-XP-Change": String(delta), "X-XP-Total": String(experience().totalXp), "X-XP-Weekly-Bonus-Change": String(delta === 0 ? 0 : met ? 50 : -50), "X-XP-Level-Up": "false" });
      }
      if (url.startsWith("/api/motivation/")) {
        const date = url.slice(-10);
        return json({ date, today: "2026-09-21", experience: experience(), activities: [{ id: "exercise", name: "Exercise", description: "Did you exercise?", kind: "manual", points: 10, cutoffTime: null, isActive: true, completed: met, earned: met ? 10 : 0, canReport: date <= "2026-09-21" }] });
      }
      throw new Error(`Unexpected request ${url}`);
    });
    vi.stubGlobal("fetch", fetchMock);
  });
  afterEach(() => { cleanup(); vi.useRealTimers(); vi.unstubAllGlobals(); resetCsrf(); });

  it("selects a past day without an entry and adds/removes XP and weekly bonus when ticked/unticked", async () => {
    const user = userEvent.setup(); render(<JournalGoals />);
    await user.click(screen.getByRole("button", { name: "Previous week" }));
    await user.click(await screen.findByRole("button", { name: "Edit goals for 2026-09-14" }));
    const checkbox = await screen.findByRole("checkbox", { name: "Exercise met on 2026-09-14" });
    expect((checkbox as HTMLInputElement).checked).toBe(false);
    await user.click(checkbox);
    await waitFor(() => expect((screen.getByRole("checkbox", { name: "Exercise met on 2026-09-14" }) as HTMLInputElement).checked).toBe(true));
    expect(await screen.findByText(/Weekly bonus earned: 50 XP/)).toBeTruthy();
    expect(await screen.findByText(/Includes a 50 XP weekly bonus/)).toBeTruthy();
    const [, request] = fetchMock.mock.calls.find(([url, init]) => url === "/api/motivation/2026-09-14/activities/exercise" && init.method === "PUT")!;
    expect(JSON.parse(request.body)).toEqual({ completed: true });
    expect(request.headers.get("X-CSRF-TOKEN")).toBe("csrf-token");
    await user.click(screen.getByRole("checkbox", { name: "Exercise met on 2026-09-14" }));
    expect(await screen.findByText(/The 50 XP weekly bonus was removed/)).toBeTruthy();
    expect(await screen.findByText(/Weekly bonus: 0 XP/)).toBeTruthy();
    await waitFor(() => expect((screen.getByRole("checkbox", { name: "Exercise met on 2026-09-14" }) as HTMLInputElement).checked).toBe(false));
  });

  it("keeps saved completion and bonus unchanged when a correction fails", async () => {
    failSave = true;
    const user = userEvent.setup(); render(<JournalGoals />);
    await user.click(screen.getByRole("button", { name: "Previous week" }));
    const checkbox = await screen.findByRole("checkbox", { name: "Exercise met on 2026-09-14" });
    await user.click(checkbox);
    expect(await screen.findByRole("alert")).toBeTruthy();
    expect(screen.getByText(/Could not save this goal/)).toBeTruthy();
    expect((checkbox as HTMLInputElement).checked).toBe(false);
    expect(screen.getByText(/Weekly bonus: 0 XP/)).toBeTruthy();
  });

  it("allows today but disables future goal dates", async () => {
    render(<JournalGoals />);
    expect((await screen.findByRole("button", { name: "Edit goals for 2026-09-21" }) as HTMLButtonElement).disabled).toBe(false);
    expect((screen.getByRole("button", { name: "Edit goals for 2026-09-22" }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole("button", { name: "Next week" }) as HTMLButtonElement).disabled).toBe(true);
  });
});
