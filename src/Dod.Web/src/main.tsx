import { StrictMode, useEffect, useState, type FormEvent } from "react";
import { createRoot } from "react-dom/client";
import { localDate } from "./date";
import { kilogramsLost } from "./weight";
import "./style.css";
import { EatenForm } from "./calories/EatenForm";
import { CalorieBalance } from "./calories/CalorieBalance";

const revision = import.meta.env.VITE_APP_REVISION?.trim();
const buildLabel = import.meta.env.DEV
  ? "Development"
  : revision
    ? `Build ${revision.slice(0, 7)}`
    : "Local build";

type Entry = {
  date: string;
  weightKg: number | null;
  caloriesBurned: number | null;
  caloriesEaten: number | null;
};
async function loadEntries(): Promise<Entry[]> {
  const response = await fetch("/api/entries/");
  if (!response.ok)
    throw new Error("Could not load your entries. Please try again.");
  return response.json();
}
function App() {
  const [entries, setEntries] = useState<Entry[]>([]);
  const [date, setDate] = useState(localDate());
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  async function refresh() {
    try {
      setEntries(await loadEntries());
      setError("");
    } catch (error) {
      setError((error as Error).message);
    } finally {
      setLoading(false);
    }
  }
  useEffect(() => {
    void refresh();
  }, []);
  const selected = entries.find((entry) => entry.date === date);
  const lost = kilogramsLost(entries);
  return (
    <main>
      <header>
        <a className="brand" href="/">
          dod<span>discipline over dopamine</span>
        </a>
        <span className="badge">ONE DAY AT A TIME</span>
      </header>
      <section className="intro">
        <p className="eyebrow">YOUR DAILY CHECK-IN</p>
        <h1>
          Small steps.
          <br />
          Steady progress.
        </h1>
        <p>
          A moment in the morning. A moment in the evening.
          <br />
          Make showing up a habit.
        </p>
      </section>
      {!loading && !error && (
        <section className="weight-summary" aria-label="Weight progress">
          <p className="eyebrow">SINCE YOUR FIRST WEIGHT ENTRY</p>
          <p className="weight-total" role="status">
            <strong>{lost === null ? "—" : lost.toFixed(2)}</strong>{" "}
            <span>kg lost</span>
          </p>
          <p className="weight-summary-note">
            {lost === null
              ? "Record your first weight to start tracking progress."
              : lost < 0
                ? "A negative number means weight gained."
                : "Your first recorded weight minus your latest recorded weight."}
          </p>
        </section>
      )}
      <div className="date-row">
        <h2>Log your day</h2>
        <label>
          Date{" "}
          <input
            type="date"
            required
            value={date}
            onChange={(e) => {
              if (e.target.value) setDate(e.target.value);
            }}
          />
        </label>
      </div>
      {error && (
        <p role="alert" className="error">
          {error} <button onClick={() => void refresh()}>Retry</button>
        </p>
      )}
      {loading ? (
        <p role="status">Loading your journal…</p>
      ) : (
        <div className="cards">
          <EntryForm
            key={`${date}-weight`}
            date={date}
            kind="weight"
            initial={selected?.weightKg ?? null}
            onSaved={refresh}
          />
          <EntryForm
            key={`${date}-calories`}
            date={date}
            kind="calories"
            initial={selected?.caloriesBurned ?? null}
            onSaved={refresh}
          />
          <EatenForm
            key={`${date}-eaten`}
            date={date}
            initial={selected?.caloriesEaten ?? null}
            onSaved={refresh}
          />
        </div>
      )}
      {!loading && !error && (
        <CalorieBalance entries={entries} through={date} />
      )}
      <section className="history">
        <div className="history-heading">
          <div>
            <p className="eyebrow">THE BIG PICTURE</p>
            <h2>Your journal</h2>
          </div>
          <span>{entries.length} days recorded</span>
        </div>
        {entries.length === 0 ? (
          <div className="empty">
            Your story starts with one check-in.
            <br />
            <span>Add your weight or calories above.</span>
          </div>
        ) : (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Date</th>
                  <th>Weight</th>
                  <th>Calories burned</th>
                  <th>Calories eaten</th>
                  <th>
                    <span className="sr-only">Actions</span>
                  </th>
                </tr>
              </thead>
              <tbody>
                {entries.map((entry) => (
                  <tr key={entry.date}>
                    <td>{entry.date}</td>
                    <td>
                      {entry.weightKg === null ? "—" : `${entry.weightKg} kg`}
                    </td>
                    <td>
                      {entry.caloriesBurned === null
                        ? "—"
                        : `${entry.caloriesBurned} kcal`}
                    </td>
                    <td>
                      {entry.caloriesEaten === null
                        ? "Daily reference"
                        : `${entry.caloriesEaten} kcal`}
                    </td>
                    <td>
                      <button
                        className="link"
                        onClick={() => {
                          setDate(entry.date);
                          window.scrollTo({ top: 0, behavior: "smooth" });
                        }}
                      >
                        Edit<span className="sr-only"> {entry.date}</span>
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
      <footer>
        Consistency over perfection.
        <span className="build-version" title={revision || undefined}>
          {buildLabel}
        </span>
      </footer>
    </main>
  );
}
function EntryForm({
  date,
  kind,
  initial,
  onSaved,
}: {
  date: string;
  kind: "weight" | "calories";
  initial: number | null;
  onSaved: () => Promise<void>;
}) {
  const weight = kind === "weight";
  const [value, setValue] = useState(initial?.toString() ?? "");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    setValue(initial?.toString() ?? "");
  }, [initial]);
  async function save(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setMessage("");
    setFailed(false);
    try {
      const response = await fetch(`/api/entries/${date}/${kind}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(
          weight
            ? { weightKg: Number(value) }
            : { caloriesBurned: Number(value) },
        ),
      });
      if (!response.ok)
        throw new Error("Could not save. Check your value and try again.");
      setMessage("Saved for " + date);
      await onSaved();
    } catch (error) {
      setFailed(true);
      setMessage((error as Error).message);
    } finally {
      setBusy(false);
    }
  }
  return (
    <form className={`card ${kind}`} onSubmit={save}>
      <div className="card-top">
        <span className="icon" aria-hidden="true">
          {weight ? "☀" : "☾"}
        </span>
        <span className="eyebrow">
          {weight ? "MORNING RITUAL" : "EVENING REFLECTION"}
        </span>
      </div>
      <h3>{weight ? "How much do you weigh?" : "What did you burn today?"}</h3>
      <p>
        {weight
          ? "Record your morning weight in kilograms."
          : "Record calories burned in kilocalories (kcal)."}
      </p>
      <label htmlFor={kind}>{weight ? "Weight" : "Calories burned"}</label>
      <div className="number-field">
        <input
          id={kind}
          type="number"
          inputMode={weight ? "decimal" : "numeric"}
          min={weight ? "0.01" : "0"}
          max={weight ? "1000" : "100000"}
          step={weight ? "0.01" : "1"}
          placeholder={weight ? "72.5" : "2400"}
          required
          value={value}
          onChange={(event) => {
            setValue(event.target.value);
            setMessage("");
          }}
        />
        <span>{weight ? "kg" : "kcal"}</span>
      </div>
      <button disabled={busy}>
        {busy ? "Saving…" : `Save ${weight ? "weight" : "calories"}`}
      </button>
      <p
        className={failed ? "error status" : "status"}
        role={failed ? "alert" : "status"}
      >
        {message ||
          (initial !== null
            ? "Already recorded · you can update it anytime"
            : "Not recorded yet")}
      </p>
    </form>
  );
}
createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
