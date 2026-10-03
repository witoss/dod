import { Brand } from "../Brand";
import { ForgotPasswordForm } from "./PasswordReset";
import { useEffect, useState, type FormEvent, type ReactNode } from "react";
import { api, apiFetch, resetCsrf } from "../api";
import "./social.css";
export type User = {
  id: string;
  nickname: string;
  hasAvatar: boolean;
  isAdmin?: boolean;
};
export function Avatar({
  user,
  revision = 0,
}: {
  user: User;
  revision?: number;
}) {
  return user.hasAvatar ? (
    <img
      className="avatar"
      src={`/api/avatars/${user.id}?v=${revision}`}
      alt={`${user.nickname}'s avatar`}
    />
  ) : (
    <span className="avatar avatar-placeholder" aria-hidden="true">
      {user.nickname[0].toUpperCase()}
    </span>
  );
}
export function AccountGate({
  children,
}: {
  children: (
    user: User,
    refresh: () => Promise<void>,
    logout: () => Promise<void>,
  ) => ReactNode;
}) {
  const [user, setUser] = useState<User | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [mode, setMode] = useState<"login" | "register" | "claim" | "forgot">("login");
  const [busy, setBusy] = useState(false);
  async function refresh() {
    resetCsrf();
    try {
      const response = await fetch("/api/account/me");
      if (response.status === 401) setUser(null);
      else if (response.ok) {
        setUser(await response.json());
        setError("");
      } else
        throw new Error("Could not load your account. Please reload the page.");
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setLoading(false);
    }
  }
  useEffect(() => {
    void refresh();
    const expired = () => {
      setUser(null);
      resetCsrf();
      setError("Your session expired. Please sign in again.");
    };
    window.addEventListener("dod-session-expired", expired);
    return () => window.removeEventListener("dod-session-expired", expired);
  }, []);
  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    setBusy(true);
    setError("");
    const data = new FormData(e.currentTarget);
    try {
      await api(`/api/account/${mode}`, "POST", {
        nickname: data.get("nickname"),
        password: data.get("password"),
        ownerPassword: data.get("ownerPassword"),
      });
      await refresh();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  async function logout() {
    await api("/api/account/logout", "POST");
    setUser(null);
    resetCsrf();
  }
  if (loading)
    return (
      <main>
        <p role="status">Loading your account…</p>
      </main>
    );
  if (user) return children(user, refresh, logout);
  if (mode === "forgot") return <main className="account-page">
    <Brand /><ForgotPasswordForm />
    <button onClick={() => { setMode("login"); setError(""); }}>Back to sign in</button>
  </main>;
  return (
    <main className="account-page">
      <Brand />
      <h1>
        Your progress.
        <br />
        Together.
      </h1>
      <div className="social-tabs">
        {(["login", "register", "claim"] as const).map((m) => (
          <button
            key={m}
            type="button"
            aria-pressed={mode === m}
            onClick={() => {
              setMode(m);
              setError("");
            }}
          >
            {m === "login"
              ? "Sign in"
              : m === "register"
                ? "Create account"
                : "Claim existing journal"}
          </button>
        ))}
      </div>
      <form className="social-panel" onSubmit={submit} key={mode}>
        <h2>
          {mode === "login"
            ? "Welcome back"
            : mode === "claim"
              ? "Keep your original journal"
              : "Create your account"}
        </h2>
        {mode === "claim" && (
          <p>
            For the original owner only. Verify the existing tracker password
            and choose your new personal login. Your entries stay with you.
          </p>
        )}
        <label>
          Nickname
          <input
            name="nickname"
            required
            pattern="[a-zA-Z0-9_]{3,24}"
            maxLength={24}
            autoComplete="username"
          />
        </label>
        <p className="hint">
          3–24 letters, numbers or underscores. Nicknames are unique, ignoring
          letter case.
        </p>
        <label>
          {mode === "login" ? "Password" : "New account password"}
          <input
            type="password"
            name="password"
            required
            minLength={mode === "login" ? 1 : 12}
            maxLength={128}
            autoComplete={
              mode === "login" ? "current-password" : "new-password"
            }
          />
        </label>
        {mode === "claim" && (
          <label>
            Existing tracker password
            <input
              type="password"
              name="ownerPassword"
              required
              autoComplete="off"
            />
          </label>
        )}
        {error && (
          <p role="alert" className="error">
            {error}
          </p>
        )}
        <button disabled={busy}>
          {busy
            ? "Please wait…"
            : mode === "login"
              ? "Sign in"
              : mode === "register"
                ? "Create account"
                : "Claim journal"}
        </button>
      </form>
      {mode === "login" && <button className="link" onClick={() => { setMode("forgot"); setError(""); }}>Forgot password?</button>}
    </main>
  );
}
export function Profile({
  user,
  refresh,
}: {
  user: User;
  refresh: () => Promise<void>;
}) {
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const [revision, setRevision] = useState(0);
  async function save(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const form = e.currentTarget;
    const data = new FormData(form);
    setBusy(true);
    setError("");
    setMessage("");
    try {
      await api("/api/account/profile", "PUT", {
        nickname: data.get("nickname"),
      });
      await refresh();
      setMessage("Nickname saved.");
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  async function upload(file?: File) {
    if (!file) return;
    setError("");
    setMessage("");
    if (
      !["image/jpeg", "image/png"].includes(file.type) ||
      file.size > 1024 * 1024
    ) {
      setError("Choose a PNG or JPEG up to 1 MB.");
      return;
    }
    setBusy(true);
    try {
      const response = await apiFetch("/api/account/avatar", {
        method: "PUT",
        headers: { "Content-Type": file.type },
        body: file,
      });
      if (!response.ok)
        throw new Error((await response.json()).detail || "Upload failed.");
      setRevision(Date.now());
      await refresh();
      setMessage("Avatar saved.");
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  async function remove() {
    setBusy(true);
    setError("");
    setMessage("");
    try {
      await api("/api/account/avatar", "DELETE");
      await refresh();
      setMessage("Avatar removed.");
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  return (
    <section className="social-panel">
      <h2>Your profile</h2>
      <Avatar user={user} revision={revision} />
      <form onSubmit={save}>
        <label>
          Nickname
          <input
            name="nickname"
            defaultValue={user.nickname}
            required
            pattern="[a-zA-Z0-9_]{3,24}"
            maxLength={24}
          />
        </label>
        <button disabled={busy}>Save nickname</button>
      </form>
      <label>
        Upload avatar (PNG or JPEG, up to 1 MB and 2048 × 2048 pixels)
        <input
          disabled={busy}
          type="file"
          accept="image/png,image/jpeg"
          onChange={(e) => {
            void upload(e.target.files?.[0]);
            e.target.value = "";
          }}
        />
      </label>
      <p className="hint">
        Your nickname and avatar are visible to signed-in users who know your
        nickname and to challenge participants.
      </p>
      {user.hasAvatar && (
        <button disabled={busy} onClick={() => void remove()}>
          Remove avatar
        </button>
      )}
      {message && <p role="status">{message}</p>}
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
    </section>
  );
}
