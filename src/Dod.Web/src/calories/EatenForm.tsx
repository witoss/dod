import { useEffect, useState, type FormEvent } from "react";

export function EatenForm({
  date,
  initial,
  onSaved,
}: {
  date: string;
  initial: number | null;
  onSaved: () => Promise<void>;
}) {
  const [value, setValue] = useState(initial?.toString() ?? "");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");
  useEffect(() => {
    setValue(initial?.toString() ?? "");
  }, [initial]);
  async function save(caloriesEaten: number | null) {
    setBusy(true);
    setMessage("");
    setError("");
    try {
      const response = await fetch(`/api/entries/${date}/eaten`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ caloriesEaten }),
      });
      if (!response.ok)
        throw new Error("Could not save calories eaten. Please try again.");
      setValue(caloriesEaten?.toString() ?? "");
      setMessage(
        caloriesEaten === null
          ? "This day now uses your daily reference."
          : `Calories eaten saved for ${date}.`,
      );
      await onSaved();
    } catch (error) {
      setError((error as Error).message);
    } finally {
      setBusy(false);
    }
  }
  function submit(event: FormEvent) {
    event.preventDefault();
    const amount = value.trim() === "" ? null : Number(value);
    if (
      amount !== null &&
      (!Number.isInteger(amount) || amount < 0 || amount > 100000)
    ) {
      setError("Enter a whole number from 0 to 100000 kcal.");
      return;
    }
    void save(amount);
  }
  return (
    <form className="card eaten-card" onSubmit={submit}>
      <p className="eyebrow">OPTIONAL DAILY OVERRIDE</p>
      <h3>What did you eat today?</h3>
      <p>
        Leave this blank to use your daily calorie reference. Enter a value when
        your intake differs.
      </p>
      <label htmlFor="calories-eaten">Calories eaten</label>
      <div className="number-field">
        <input
          id="calories-eaten"
          type="number"
          inputMode="numeric"
          min="0"
          max="100000"
          step="1"
          placeholder="Use daily reference"
          value={value}
          disabled={busy}
          onChange={(event) => {
            setValue(event.target.value);
            setMessage("");
            setError("");
          }}
        />
        <span>kcal</span>
      </div>
      <div className="eaten-actions">
        <button disabled={busy}>
          {busy ? "Saving…" : "Save calories eaten"}
        </button>
        <button
          type="button"
          className="link"
          disabled={busy || initial === null}
          onClick={() => void save(null)}
        >
          Use reference
        </button>
      </div>
      <p className="status" role="status">
        {message ||
          (initial === null
            ? "Using the daily reference"
            : "Using your recorded intake for this day")}
      </p>
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
    </form>
  );
}
