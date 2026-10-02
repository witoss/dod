import { apiFetch } from "../api";
import { useEffect, useRef, useState, type FormEvent } from "react";
import {
  balanceDays,
  balanceDescription,
  signedCalories,
  type BalanceDay,
  type CalorieMeasurement,
} from "./balance";
import "./calories.css";
import { CumulativeChart } from "./CumulativeChart";

type Reference = { calories: number | null };
const referenceUrl = "/api/settings/calorie-reference";

export function CalorieBalance({
  entries,
  through,
}: {
  entries: CalorieMeasurement[];
  through: string;
}) {
  const [reference, setReference] = useState<number | null>(null);
  const [value, setValue] = useState("");
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [loadError, setLoadError] = useState("");
  const [saveError, setSaveError] = useState("");
  const [message, setMessage] = useState("");
  const [range, setRange] = useState(14);

  async function loadReference() {
    setLoading(true);
    setLoadError("");
    try {
      const response = await apiFetch(referenceUrl);
      if (!response.ok)
        throw new Error("Could not load your calorie reference. Please retry.");
      const settings: Reference = await response.json();
      setReference(settings.calories);
      setValue(settings.calories?.toString() ?? "");
    } catch (error) {
      setLoadError((error as Error).message);
    } finally {
      setLoading(false);
    }
  }
  useEffect(() => {
    void loadReference();
  }, []);

  async function saveReference(event: FormEvent) {
    event.preventDefault();
    const calories = Number(value);
    if (!Number.isInteger(calories) || calories < 1 || calories > 100000) {
      setSaveError("Enter a whole number from 1 to 100000 kcal.");
      return;
    }
    setBusy(true);
    setMessage("");
    setSaveError("");
    try {
      const response = await apiFetch(referenceUrl, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ calories }),
      });
      if (!response.ok)
        throw new Error("Could not save your reference. Please try again.");
      setReference(calories);
      setMessage("Reference saved. All daily comparisons are updated.");
    } catch (error) {
      setSaveError((error as Error).message);
    } finally {
      setBusy(false);
    }
  }

  const days =
    reference === null ? [] : balanceDays(entries, reference, through, range);
  const recorded = days.filter((day) => day.balance !== null);
  const average =
    recorded.length === 0
      ? null
      : Math.round(
          recorded.reduce((sum, day) => sum + day.balance!, 0) /
            recorded.length,
        );
  return (
    <section className="calorie-section" aria-labelledby="balance-heading">
      <div className="balance-heading">
        <div>
          <p className="eyebrow">FIND YOUR RHYTHM</p>
          <h2 id="balance-heading">Your calorie balance</h2>
          <p className="balance-description">
            Compare calories eaten with calories burned.
          </p>
        </div>
        <span className="balance-unit">kcal / day</span>
      </div>
      {loading ? (
        <p role="status">Loading your reference…</p>
      ) : loadError ? (
        <p className="error" role="alert">
          {loadError}{" "}
          <button onClick={() => void loadReference()}>Retry</button>
        </p>
      ) : (
        <>
          <form className="reference-form" onSubmit={saveReference}>
            <div className="reference-copy">
              <label htmlFor="calorie-reference">Daily calorie reference</label>
              <p>
                Default calories eaten for days without an intake entry.
                Changing it recalculates only those days.
              </p>
            </div>
            <div className="reference-controls">
              <div className="reference-input">
                <input
                  id="calorie-reference"
                  type="number"
                  inputMode="numeric"
                  min="1"
                  max="100000"
                  step="1"
                  required
                  placeholder="e.g. 2200"
                  value={value}
                  disabled={busy}
                  onChange={(event) => {
                    setValue(event.target.value);
                    setMessage("");
                    setSaveError("");
                  }}
                />
                <span>kcal</span>
              </div>
              <button disabled={busy}>
                {busy ? "Saving…" : "Save reference"}
              </button>
            </div>
          </form>
          {saveError && (
            <p role="alert" className="error">
              {saveError}
            </p>
          )}
          <p className="reference-status" role="status">
            {message}
          </p>
          {reference === null ? (
            <div className="empty">
              Set your reference to reveal your daily balance.
              <br />
              <span>
                Your existing calorie entries will appear automatically.
              </span>
            </div>
          ) : (
            <>
              <div className="chart-toolbar">
                <p>
                  Daily difference <span>· ending {through}</span>
                </p>
                <div className="range-control" aria-label="Chart period">
                  {[7, 14, 30].map((count) => (
                    <button
                      key={count}
                      type="button"
                      aria-pressed={range === count}
                      onClick={() => setRange(count)}
                    >
                      {count} days
                    </button>
                  ))}
                </div>
              </div>
              <div className="balance-stats">
                <div>
                  <span>Daily reference</span>
                  <strong>
                    {reference.toLocaleString()} <small>kcal</small>
                  </strong>
                </div>
                <div>
                  <span>Average difference</span>
                  <strong>
                    {average === null ? "—" : signedCalories(average)}{" "}
                    <small>{average === null ? "" : "kcal"}</small>
                  </strong>
                </div>
                <div>
                  <span>Days recorded</span>
                  <strong>
                    {recorded.length} <small>/ {range}</small>
                  </strong>
                </div>
              </div>
              {recorded.length === 0 ? (
                <div className="empty">
                  No calories recorded in this period.
                  <br />
                  <span>
                    Log a day above, change the date, or choose a wider period.
                  </span>
                </div>
              ) : (
                <BalanceChart
                  key={`${through}-${range}`}
                  days={days}
                  reference={reference}
                />
              )}
              <CumulativeChart days={days} />
              <p className="balance-note">
                Balance = calories eaten − calories burned. Days without an
                intake entry use the reference as estimated intake. Days without
                calories burned are excluded.
              </p>
            </>
          )}
        </>
      )}
    </section>
  );
}

function BalanceChart({
  days,
  reference,
}: {
  days: BalanceDay[];
  reference: number;
}) {
  const plotRef = useRef<HTMLDivElement>(null);
  const [chartWidth, setChartWidth] = useState(840);
  useEffect(() => {
    const observer = new ResizeObserver(([entry]) =>
      setChartWidth(Math.max(280, entry.contentRect.width)),
    );
    if (plotRef.current) observer.observe(plotRef.current);
    return () => observer.disconnect();
  }, []);
  const [activeDate, setActiveDate] = useState<string | null>(null);
  const active =
    days.find((day) => day.date === activeDate) ??
    [...days].reverse().find((day) => day.balance !== null)!;
  const max = Math.max(100, ...days.map((day) => Math.abs(day.balance ?? 0)));
  const limit =
    Math.ceil(max / (max > 1000 ? 500 : 100)) * (max > 1000 ? 500 : 100);
  const left = 72,
    width = chartWidth - left - 24,
    zero = 156,
    halfHeight = 108;
  const labelStride = Math.ceil(
    days.length / Math.max(1, Math.floor(width / 65)),
  );
  const slot = width / days.length;
  const barWidth = Math.min(34, slot * 0.65);
  const ticks = [limit, limit / 2, 0, -limit / 2, -limit];
  const dateLabel = (date: string) =>
    new Date(`${date}T12:00:00`).toLocaleDateString(undefined, {
      month: "short",
      day: "numeric",
    });
  return (
    <>
      <div className="chart-legend">
        <span>
          <i className="above-dot" />
          Burned more than eaten (−)
        </span>
        <span>
          <i className="below-dot" />
          Burned less than eaten (+)
        </span>
        <span>
          <i className="missing-dot" />
          Not recorded
        </span>
      </div>
      <div
        ref={plotRef}
        className="balance-plot"
        role="group"
        aria-label="Daily calorie balance chart. Focus or hover on a day for details."
      >
        <svg
          viewBox={`0 0 ${chartWidth} 324`}
          aria-label="Bars show calories eaten minus calories burned, with zero at the center"
        >
          <text x="12" y="18" className="axis-caption">
            kcal
          </text>
          {ticks.map((tick) => {
            const y = zero - (tick / limit) * halfHeight;
            return (
              <g key={tick}>
                <line
                  x1={left}
                  y1={y}
                  x2={left + width}
                  y2={y}
                  className={tick === 0 ? "zero-line" : "grid-line"}
                />
                <text
                  x={left - 12}
                  y={y + 4}
                  textAnchor="end"
                  className="axis-label"
                >
                  {signedCalories(tick)}
                </text>
              </g>
            );
          })}
          {days.map((day, index) => {
            const x = left + slot * index + slot / 2;
            const height = (Math.abs(day.balance ?? 0) / limit) * halfHeight;
            const y =
              day.balance !== null && day.balance > 0 ? zero - height : zero;
            const description =
              day.balance === null
                ? "Not recorded"
                : `${day.caloriesBurned!.toLocaleString()} kcal burned; ${balanceDescription(day.balance)}`;
            return (
              <g
                key={day.date}
                tabIndex={0}
                role="button"
                aria-label={`${day.date}: ${description}`}
                onFocus={() => setActiveDate(day.date)}
                onMouseEnter={() => setActiveDate(day.date)}
                onClick={() => setActiveDate(day.date)}
                onKeyDown={(event) => {
                  if (event.key === "Enter" || event.key === " ") {
                    event.preventDefault();
                    setActiveDate(day.date);
                  }
                }}
                className="chart-day"
              >
                <title>
                  {day.date}: {description}
                </title>
                <rect
                  x={x - slot / 2 + 1}
                  y={30}
                  width={slot - 2}
                  height={252}
                  rx={6}
                  className={
                    day.date === active.date
                      ? "day-highlight active"
                      : "day-highlight"
                  }
                />
                {day.balance === null ? (
                  <circle cx={x} cy={zero} r={3} className="missing-point" />
                ) : day.balance === 0 ? (
                  <rect
                    x={x - barWidth / 2}
                    y={zero - 2}
                    width={barWidth}
                    height={4}
                    rx={2}
                    className="zero-bar"
                  />
                ) : (
                  <rect
                    x={x - barWidth / 2}
                    y={y}
                    width={barWidth}
                    height={Math.max(height, 2)}
                    rx={4}
                    className={day.balance < 0 ? "above-bar" : "below-bar"}
                  />
                )}
                {((index % labelStride === 0 &&
                  index < days.length - labelStride) ||
                  index === days.length - 1) && (
                  <text
                    x={x}
                    y={305}
                    textAnchor="middle"
                    className="axis-label"
                  >
                    {dateLabel(day.date)}
                  </text>
                )}
              </g>
            );
          })}
        </svg>
      </div>
      <div className="day-detail" aria-live="polite" aria-atomic="true">
        <span>
          {dateLabel(active.date)}
          <small>{active.date}</small>
        </span>
        <span>
          {active.caloriesBurned === null
            ? "No calorie entry"
            : `${active.caloriesBurned.toLocaleString()} kcal burned`}
          <small>
            Eaten: {(active.caloriesEaten ?? reference).toLocaleString()} kcal (
            {active.caloriesEaten == null ? "reference" : "recorded"})
          </small>
        </span>
        <strong
          className={
            active.balance === null || active.balance === 0
              ? ""
              : active.balance < 0
                ? "above-text"
                : "below-text"
          }
        >
          {active.balance === null
            ? "Not recorded"
            : balanceDescription(active.balance)}
        </strong>
      </div>
      <details className="balance-table">
        <summary>View daily balances as a table</summary>
        <div className="table-wrap">
          <table>
            <caption className="sr-only">
              Daily eaten and burned calories, using {reference} kcal as the
              default intake
            </caption>
            <thead>
              <tr>
                <th scope="col">Date</th>
                <th scope="col">Burned</th>
                <th scope="col">Eaten</th>
                <th scope="col">Balance</th>
              </tr>
            </thead>
            <tbody>
              {[...days].reverse().map((day) => (
                <tr key={day.date}>
                  <td>{day.date}</td>
                  <td>
                    {day.caloriesBurned === null
                      ? "Not recorded"
                      : `${day.caloriesBurned.toLocaleString()} kcal`}
                  </td>
                  <td>
                    {(day.caloriesEaten ?? reference).toLocaleString()} kcal (
                    {day.caloriesEaten == null ? "reference" : "recorded"})
                  </td>
                  <td>
                    {day.balance === null
                      ? "—"
                      : balanceDescription(day.balance)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </details>
    </>
  );
}
