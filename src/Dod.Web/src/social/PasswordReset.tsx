import { useState, type FormEvent } from "react";
import { api, resetCsrf } from "../api";
import { Brand } from "../Brand";

export function ForgotPasswordForm() {
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const nickname = new FormData(event.currentTarget).get("nickname");
    setBusy(true); setMessage(""); setError("");
    try {
      const result = await api<{ detail: string }>("/api/account/forgot-password", "POST", { nickname });
      setMessage(result.detail);
    } catch (e) { setError((e as Error).message); }
    finally { setBusy(false); }
  }
  return <form className="social-panel" onSubmit={submit}>
    <h2>Forgot your password?</h2>
    <p>Enter your nickname. Password recovery is available only if you previously saved and confirmed an email address in Profile. Weekly emails can be turned off.</p>
    <label>Nickname<input name="nickname" required maxLength={24} pattern="[a-zA-Z0-9_]{3,24}" autoComplete="username" disabled={busy} /></label>
    <button disabled={busy}>{busy ? "Please wait…" : "Send reset link"}</button>
    {message && <p role="status">{message} Check your inbox and spam folder; the link expires in 30 minutes.</p>}
    {error && <p role="alert" className="error">{error}</p>}
  </form>;
}

export function ResetPasswordPage({ token }: { token: string }) {
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState(false);
  const [error, setError] = useState("");
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    const password = data.get("password");
    setError("");
    if (password !== data.get("confirmPassword")) { setError("Passwords do not match."); return; }
    setBusy(true);
    try {
      await api("/api/account/reset-password", "POST", { token, password });
      form.reset(); resetCsrf(); setDone(true);
    } catch (e) { setError((e as Error).message); }
    finally { setBusy(false); }
  }
  return <main className="account-page">
    <Brand />
    <section className="social-panel">
      <h1>Choose a new password</h1>
      {done ? <p role="status">Your password was changed and existing sessions were signed out. Sign in with your new password.</p>
        : <form onSubmit={submit}>
          <p>Use 12–128 characters. This link expires in 30 minutes and works once.</p>
          <label>New password<input type="password" name="password" required minLength={12} maxLength={128} autoComplete="new-password" disabled={busy || !token} /></label>
          <label>Confirm new password<input type="password" name="confirmPassword" required minLength={12} maxLength={128} autoComplete="new-password" disabled={busy || !token} /></label>
          <button disabled={busy || !token}>{busy ? "Please wait…" : "Reset password"}</button>
        </form>}
      {!token && <p role="alert">This reset link is invalid. Request a new one from the sign-in page.</p>}
      {error && <p role="alert" className="error">{error}</p>}
      <p><a href="/">Back to sign in</a></p>
    </section>
  </main>;
}
