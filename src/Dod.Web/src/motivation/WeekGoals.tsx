import { useEffect, useState } from "react";
import { api } from "../api";
import { shiftDate, weekMonday } from "../date";

type WeekSummary = {
  weekStart: string;
  weekEnd: string;
  eligibleFrom: string;
  closed: boolean;
  bonusXp: number;
  activities: { id: string; name: string; days: { date: string; completed: boolean | null; xp: number }[] }[];
};

export function WeekGoals({ date, onSelectDate }: { date: string; onSelectDate: (date: string) => void }) {
  const monday = weekMonday(date);
  const today = new Date().toISOString().slice(0, 10);
  const [summary, setSummary] = useState<WeekSummary | null>(null);
  const [error, setError] = useState("");
  const [revision, setRevision] = useState(0);
  useEffect(() => {
    const refresh = () => setRevision(value => value + 1);
    window.addEventListener("dod-xp-change", refresh);
    window.addEventListener("dod-activities-changed", refresh);
    return () => {
      window.removeEventListener("dod-xp-change", refresh);
      window.removeEventListener("dod-activities-changed", refresh);
    };
  }, []);
  useEffect(() => {
    let active = true;
    setError("");
    api<WeekSummary>(`/api/motivation/weekly/${monday}`)
      .then(result => { if (active) setSummary(result); })
      .catch(e => { if (active) setError(e.message); });
    return () => { active = false; };
  }, [monday, revision]);
  const current = summary?.weekStart === monday ? summary : null;
  const days = Array.from({ length: 7 }, (_, i) => shiftDate(monday, i));
  return (
    <section className="week-goals social-panel" aria-label="Weekly goals and past days">
      <div className="history-heading">
        <div><p className="eyebrow">YOUR WEEKLY GOALS</p><h2>{monday} – {shiftDate(monday, 6)}</h2></div>
        <nav className="week-navigation" aria-label="Choose goal week">
          <button onClick={() => onSelectDate(shiftDate(date, -7))}>Previous week</button>
          <button onClick={() => onSelectDate(today)}>This week</button>
          <button disabled={shiftDate(monday, 7) > today} onClick={() => onSelectDate(shiftDate(monday, 7))}>Next week</button>
        </nav>
      </div>
      <p>Select a day below to add or correct its goals, even if you did not record weight or calories. Corrections update your XP and that week’s bonus.</p>
      {error && <p role="alert" className="error">{error} <button onClick={() => setRevision(value => value + 1)}>Retry weekly goals</button></p>}
      {!current && !error && <p role="status">Loading weekly goals…</p>}
      <div className="table-wrap">
        <table className="week-goal-grid">
          <caption>Weekly roster · UTC dates · ✓ met, × not met, — not reported</caption>
          <thead><tr><th scope="col">Goal</th>{days.map((day, i) => (
            <th scope="col" key={day}>
              <button disabled={day > today} aria-pressed={day === date} aria-label={`Edit goals for ${day}`} onClick={() => onSelectDate(day)}>
                {["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"][i]}<br />{day.slice(5)}
              </button>
            </th>
          ))}</tr></thead>
          <tbody>{current?.activities.map(activity => (
            <tr key={activity.id}><th scope="row">{activity.name}</th>{activity.days.map(day => (
              <td key={day.date} className={day.completed === true ? "goal-complete" : day.completed === false ? "goal-incomplete" : ""}
                aria-label={`${activity.name}, ${day.date}: ${day.completed === true ? "met" : day.completed === false ? "not met" : "not reported"}, ${day.xp} XP`}>
                {day.completed === true ? "✓" : day.completed === false ? "×" : "—"}<small>{day.xp} XP</small>
              </td>
            ))}</tr>
          ))}</tbody>
        </table>
      </div>
      {current && <p role="status" className="weekly-bonus">
        {monday < current.eligibleFrom ? `Weekly bonus eligibility starts ${current.eligibleFrom}.`
          : !current.closed ? "Week in progress. A 50 XP bonus is awarded after Sunday if every weekly goal was met on all seven days."
          : current.bonusXp === 50 ? "Weekly bonus earned: 50 XP. Correcting a missed goal removes this bonus."
          : "Weekly bonus: 0 XP. Complete every weekly goal on all seven days to earn 50 XP."}
      </p>}
      {current?.activities.length === 0 && <p>No weekly goals for this week.</p>}
    </section>
  );
}
