import { useEffect, useId, useRef, useState } from "react";
import "./weight-chart.css";
export type ParticipantWeights = {
  id: string;
  nickname: string;
  weights: { date: string; weightKg: number }[];
};
const day = (date: string) => Date.parse(`${date}T00:00:00Z`) / 86400000;
const colors = [
  "#246653",
  "#ad4a24",
  "#4569b5",
  "#9b4589",
  "#82700f",
  "#087b87",
];
export function WeightChart({
  participants,
  startDate,
  endDate,
}: {
  participants: ParticipantWeights[];
  startDate: string;
  endDate: string;
}) {
  const titleId = useId();
  const container = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState(700);
  const [hidden, setHidden] = useState<string[]>([]);
  const [selected, setSelected] = useState<{
    name: string;
    date: string;
    weight: number;
  } | null>(null);
  useEffect(() => {
    const observer = new ResizeObserver(([entry]) =>
      setWidth(Math.max(260, entry.contentRect.width)),
    );
    if (container.current) observer.observe(container.current);
    return () => observer.disconnect();
  }, []);
  useEffect(() => setSelected(null), [participants]);
  const visible = participants.filter((p) => !hidden.includes(p.id));
  const values = visible.flatMap((p) => p.weights.map((w) => w.weightKg));
  const allEmpty = participants.every((p) => p.weights.length === 0);
  const low = values.length ? Math.min(...values) : 0;
  const high = values.length ? Math.max(...values) : 1;
  const padding = Math.max((high - low) * 0.15, 0.5);
  const min = Math.max(0, low - padding),
    max = high + padding;
  const left = 52,
    right = width - 16,
    top = 24,
    bottom = 254;
  const start = day(startDate),
    last = day(endDate) - 1;
  const x = (date: string) =>
    left + ((day(date) - start) / Math.max(1, last - start)) * (right - left);
  const y = (weight: number) =>
    bottom - ((weight - min) / (max - min)) * (bottom - top);
  const tickDays = [
    ...new Set([
      start,
      ...(width > 480 ? [Math.round((start + last) / 2)] : []),
      last,
    ]),
  ];
  return (
    <section className="challenge-weight-chart" aria-labelledby={titleId}>
      <h3 id={titleId}>Daily participant weights</h3>
      <p className="hint">
        Latest saved weight for each day, in kilograms. Missing days are gaps.
        Tap or focus a point for details; select a name to hide or show their
        line.
      </p>
      <div className="weight-legend">
        {participants.map((p, index) => (
          <button
            key={p.id}
            type="button"
            aria-pressed={!hidden.includes(p.id)}
            onClick={() => {
              setSelected(null);
              setHidden((previous) =>
                previous.includes(p.id)
                  ? previous.filter((id) => id !== p.id)
                  : [...previous, p.id],
              );
            }}
          >
            <span
              aria-hidden="true"
              style={{ background: colors[index % colors.length] }}
            />
            {p.nickname}
            {p.weights.length === 0 ? " · no weights yet" : ""}
          </button>
        ))}
      </div>
      <div ref={container}>
        {allEmpty ? (
          <p className="empty">
            No weights recorded within this challenge yet.
          </p>
        ) : values.length === 0 ? (
          <p className="empty">
            Select a participant with recorded weights to show their line.
          </p>
        ) : (
          <svg
            viewBox={`0 0 ${width} 300`}
            width="100%"
            role="group"
            aria-label="Daily weights of challenge participants in kilograms"
          >
            {[0, 1, 2, 3, 4].map((i) => {
              const value = min + ((max - min) * i) / 4;
              return (
                <g key={i}>
                  <line
                    x1={left}
                    x2={right}
                    y1={y(value)}
                    y2={y(value)}
                    stroke="#dfe7dd"
                  />
                  <text x={left - 8} y={y(value) + 4} textAnchor="end">
                    {value.toFixed(1)}
                  </text>
                </g>
              );
            })}
            <text x={left} y={14}>
              kg
            </text>
            {tickDays.map((d, i) => {
              const date = new Date(d * 86400000).toISOString().slice(0, 10);
              return (
                <text
                  key={d}
                  x={x(date)}
                  y={280}
                  textAnchor={
                    i === 0
                      ? "start"
                      : i === tickDays.length - 1
                        ? "end"
                        : "middle"
                  }
                >
                  {date.slice(5)}
                </text>
              );
            })}
            {participants.map((p, index) =>
              hidden.includes(p.id) ? null : (
                <g key={p.id}>
                  {p.weights.map((w, i) => {
                    const prev = p.weights[i - 1];
                    return prev && day(w.date) - day(prev.date) === 1 ? (
                      <line
                        key={w.date}
                        x1={x(prev.date)}
                        y1={y(prev.weightKg)}
                        x2={x(w.date)}
                        y2={y(w.weightKg)}
                        stroke={colors[index % colors.length]}
                        strokeWidth={2.5}
                        strokeDasharray={index % 2 ? "7 3" : undefined}
                      />
                    ) : null;
                  })}
                  {p.weights.map((w) => (
                    <circle
                      key={w.date}
                      cx={x(w.date)}
                      cy={y(w.weightKg)}
                      r={5}
                      fill={colors[index % colors.length]}
                      stroke="white"
                      strokeWidth={1.5}
                      tabIndex={0}
                      role="img"
                      aria-label={`${p.nickname}, ${w.date}: ${w.weightKg.toFixed(2)} kg`}
                      onMouseEnter={() =>
                        setSelected({
                          name: p.nickname,
                          date: w.date,
                          weight: w.weightKg,
                        })
                      }
                      onFocus={() =>
                        setSelected({
                          name: p.nickname,
                          date: w.date,
                          weight: w.weightKg,
                        })
                      }
                      onClick={() =>
                        setSelected({
                          name: p.nickname,
                          date: w.date,
                          weight: w.weightKg,
                        })
                      }
                    >
                      <title>
                        {p.nickname} · {w.date} · {w.weightKg.toFixed(2)} kg
                      </title>
                    </circle>
                  ))}
                </g>
              ),
            )}
          </svg>
        )}
      </div>
      <p className="weight-point-detail" role="status">
        {selected
          ? `${selected.name} · ${selected.date} · ${selected.weight.toFixed(2)} kg`
          : "Select a recorded point to see its weight and date."}
      </p>
      {!allEmpty && (
        <details>
          <summary>View daily weights as a table</summary>
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Date</th>
                  <th>Participant</th>
                  <th>Weight (kg)</th>
                </tr>
              </thead>
              <tbody>
                {participants
                  .flatMap((p) =>
                    p.weights.map((w) => ({
                      ...w,
                      id: p.id,
                      name: p.nickname,
                    })),
                  )
                  .sort(
                    (a, b) =>
                      a.date.localeCompare(b.date) ||
                      a.name.localeCompare(b.name),
                  )
                  .map((w) => (
                    <tr key={`${w.id}-${w.date}`}>
                      <td>{w.date}</td>
                      <td>{w.name}</td>
                      <td>{w.weightKg.toFixed(2)}</td>
                    </tr>
                  ))}
              </tbody>
            </table>
          </div>
        </details>
      )}
    </section>
  );
}
