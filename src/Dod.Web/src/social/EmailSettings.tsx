import { useEffect, useState, type FormEvent } from "react";
import { api } from "../api";
import { Brand } from "../Brand";

type Preferences = {
  email: string;
  enabled: boolean;
  verified: boolean;
  sendingAvailable: boolean;
};

export function EmailSettings() {
  const [preferences, setPreferences] = useState<Preferences>();
  const [email, setEmail] = useState("");
  const [enabled, setEnabled] = useState(false);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");

  async function load() {
    const result = await api<Preferences>("/api/email/preferences");
    setPreferences(result);
    setEmail(result.email);
    setEnabled(result.enabled);
    setError("");
  }
  useEffect(() => {
    void load().catch((e) => setError(e.message));
  }, []);

  async function save(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError("");
    setMessage("");
    try {
      const result = await api<Preferences>("/api/email/preferences", "PUT", {
        email,
        enabled,
      });
      setPreferences(result);
      setEmail(result.email);
      setMessage(
        result.email && !result.verified && result.sendingAvailable
          ? "Settings saved. Check your inbox for a confirmation link (valid for 24 hours)."
          : "Email settings saved.",
      );
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  async function resend() {
    setBusy(true);
    setError("");
    setMessage("");
    try {
      await api("/api/email/resend", "POST");
      setMessage("Confirmation queued. Check your inbox and spam folder.");
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="social-panel">
      <h2>Weekly email summary</h2>
      <p>
        Your Monday recap includes weight progress, daily goals, XP, and challenge
        standings for the previous Monday–Sunday (UTC).
      </p>
      <p>
        Complete every goal in a full eligible week to earn 50 bonus XP. New
        accounts and activity changes enter the weekly rules the following Monday.
      </p>
      {preferences ? (
        <>
          {!preferences.sendingAvailable && (
            <p role="status">
              Email delivery is currently unavailable. You can save your
              preferences and request confirmation when delivery is available.
            </p>
          )}
          <form onSubmit={save}>
            <label>
              Email address
              <input
                type="email"
                maxLength={254}
                autoComplete="email"
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                disabled={busy}
              />
            </label>
            <label>
              <input
                type="checkbox"
                checked={enabled}
                onChange={(e) => setEnabled(e.target.checked)}
                disabled={busy}
              />{" "}
              Send me weekly summaries
            </label>
            <p>
              {preferences.email
                ? preferences.verified
                  ? "Saved address confirmed."
                  : "Saved address awaiting confirmation. Weekly emails start after confirmation."
                : "No email address saved."}
            </p>
            <button disabled={busy}>
              {busy ? "Please wait…" : "Save email settings"}
            </button>
          </form>
          {preferences.email && !preferences.verified && (
            <button
              disabled={busy || !preferences.sendingAvailable}
              onClick={() => void resend()}
            >
              Resend confirmation
            </button>
          )}
          <p className="hint">
            Your email address is private. Turn summaries off here or use the
            unsubscribe link in any weekly email.
          </p>
        </>
      ) : (
        !error && <p role="status">Loading email settings…</p>
      )}
      {message && <p role="status">{message}</p>}
      {error && (
        <p role="alert" className="error">
          {error}{" "}
          {!preferences && (
            <button onClick={() => void load().catch((e) => setError(e.message))}>
              Retry
            </button>
          )}
        </p>
      )}
    </section>
  );
}

// Links use fragments so bearer tokens are never sent in HTTP URLs or referrers.
export function EmailLinkAction({
  action,
  token,
}: {
  action: "verify" | "unsubscribe";
  token: string;
}) {
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState(false);
  const [error, setError] = useState("");

  async function submit() {
    setBusy(true);
    setError("");
    try {
      await api(`/api/email/${action}`, "POST", { token });
      setDone(true);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  return (
    <main className="account-page">
      <Brand />
      <section className="social-panel">
        <h1>{action === "verify" ? "Confirm your email" : "Stop weekly emails"}</h1>
        {done ? (
          <p role="status">
            {action === "verify"
              ? "Your email is confirmed. Your saved weekly summary preference now applies."
              : "Weekly emails have been turned off."}
          </p>
        ) : (
          <>
            <p>
              {action === "verify"
                ? "Confirm that this address belongs to you."
                : "Turn off weekly summaries for this email address."}
            </p>
            <button disabled={busy} onClick={() => void submit()}>
              {busy
                ? "Please wait…"
                : action === "verify"
                  ? "Confirm email"
                  : "Unsubscribe"}
            </button>
          </>
        )}
        {error && (
          <p role="alert" className="error">{error}</p>
        )}
        <p><a href="/">Open your journal</a></p>
      </section>
    </main>
  );
}
