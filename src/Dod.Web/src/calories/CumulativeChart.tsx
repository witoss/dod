import { useEffect, useRef, useState } from "react";
import { cumulativeBalances, signedCalories, type BalanceDay } from "./balance";

export function CumulativeChart({ days }: { days: BalanceDay[] }) {
  const points = cumulativeBalances(days);
  const recorded = points.filter((point) => point.cumulative !== null);
  const total = recorded.at(-1)?.cumulative ?? null;
  const [selectedDate, setSelectedDate] = useState<string | null>(null);
  const active =
    points.find((point) => point.date === selectedDate) ?? recorded.at(-1);
  const plot = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState(840);
  useEffect(() => {
    const observer = new ResizeObserver(([entry]) =>
      setWidth(Math.max(280, entry.contentRect.width)),
    );
    if (plot.current) observer.observe(plot.current);
    return () => observer.disconnect();
  }, []);
  const left = 76,
    right = width - 30,
    top = 32,
    bottom = 248;
  const maximum = Math.max(
    100,
    ...recorded.map((point) => Math.abs(point.cumulative!)),
  );
  const limit = Math.ceil(maximum / 100) * 100;
  const y = (value: number) =>
    (top + bottom) / 2 - ((value / limit) * (bottom - top)) / 2;
  const x = (index: number) =>
    left + (index / Math.max(1, points.length - 1)) * (right - left);
  const stride = Math.ceil(
    points.length / Math.max(1, Math.floor((right - left) / 70)),
  );
  const shortDate = (date: string) =>
    new Date(`${date}T12:00:00`).toLocaleDateString(undefined, {
      month: "short",
      day: "numeric",
    });
  const path = points
    .map((point, index) =>
      point.cumulative === null
        ? ""
        : `${index === 0 || points[index - 1].cumulative === null ? "M" : "L"} ${x(index)} ${y(point.cumulative)}`,
    )
    .join(" ");
  return (
    <section
      className="cumulative-section"
      aria-labelledby="cumulative-heading"
    >
      <div className="balance-heading">
        <div>
          <p className="eyebrow">THE RUNNING TOTAL</p>
          <h3 id="cumulative-heading">Cumulative balance</h3>
          <p className="balance-description">
            {points[0]?.date} – {points.at(-1)?.date} · {recorded.length} of{" "}
            {points.length} days recorded
          </p>
        </div>
        <div className="period-total">
          <span>Period total</span>
          <strong>
            {total === null ? "—" : signedCalories(total)} <small>kcal</small>
          </strong>
        </div>
      </div>
      <p className="balance-note">
        Adds calories eaten − calories burned, starting from zero for this
        period. Intake uses your recorded value when available, otherwise the
        daily reference. Days without calories burned appear as gaps and are
        excluded from the total.
      </p>
      {total === null ? (
        <div className="empty">No balance data for this period.</div>
      ) : (
        <>
          <div className="balance-plot" ref={plot}>
            <svg
              viewBox={`0 0 ${width} 300`}
              role="group"
              aria-label="Cumulative calorie balance for the selected period"
            >
              {[limit, 0, -limit].map((tick) => (
                <g key={tick}>
                  <line
                    x1={left}
                    x2={right}
                    y1={y(tick)}
                    y2={y(tick)}
                    className={tick === 0 ? "zero-line" : "grid-line"}
                  />
                  <text
                    x={left - 10}
                    y={y(tick) + 4}
                    textAnchor="end"
                    className="axis-label"
                  >
                    {signedCalories(tick)}
                  </text>
                </g>
              ))}
              <path d={path} className="cumulative-line" />
              {points.map((point, index) => (
                <g key={point.date}>
                  {point.cumulative !== null && (
                    <g
                      className="chart-day"
                      role="button"
                      tabIndex={0}
                      aria-label={`${point.date}: cumulative ${signedCalories(point.cumulative)} kcal`}
                      onMouseEnter={() => setSelectedDate(point.date)}
                      onFocus={() => setSelectedDate(point.date)}
                      onClick={() => setSelectedDate(point.date)}
                      onKeyDown={(event) => {
                        if (event.key === "Enter" || event.key === " ") {
                          event.preventDefault();
                          setSelectedDate(point.date);
                        }
                      }}
                    >
                      <title>
                        {point.date}: {signedCalories(point.cumulative)} kcal
                        cumulative
                      </title>
                      <circle
                        cx={x(index)}
                        cy={y(point.cumulative)}
                        r={12}
                        className="cumulative-hit"
                      />
                      <circle
                        cx={x(index)}
                        cy={y(point.cumulative)}
                        r={active?.date === point.date ? 5 : 3.5}
                        className="cumulative-point"
                      />
                    </g>
                  )}
                  {(index === points.length - 1 ||
                    (index % stride === 0 &&
                      index < points.length - stride)) && (
                    <text
                      x={x(index)}
                      y={281}
                      textAnchor="middle"
                      className="axis-label"
                    >
                      {shortDate(point.date)}
                    </text>
                  )}
                </g>
              ))}
            </svg>
          </div>
          {active && (
            <div className="day-detail" aria-live="polite">
              <span>{active.date}</span>
              <span>Daily: {signedCalories(active.balance!)} kcal</span>
              <strong>
                Running total: {signedCalories(active.cumulative!)} kcal
              </strong>
            </div>
          )}
          <details className="balance-table">
            <summary>View cumulative balances as a table</summary>
            <div className="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th scope="col">Date</th>
                    <th scope="col">Daily balance</th>
                    <th scope="col">Running total</th>
                  </tr>
                </thead>
                <tbody>
                  {points.map((point) => (
                    <tr key={point.date}>
                      <td>{point.date}</td>
                      <td>
                        {point.balance === null
                          ? "Not recorded"
                          : `${signedCalories(point.balance)} kcal`}
                      </td>
                      <td>
                        {point.cumulative === null
                          ? "—"
                          : `${signedCalories(point.cumulative)} kcal`}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </details>
        </>
      )}
    </section>
  );
}
