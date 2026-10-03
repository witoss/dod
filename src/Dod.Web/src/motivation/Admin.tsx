import { useEffect, useState, type FormEvent } from "react";
import { api } from "../api";
import type { Experience } from "./Motivation";
type Activity = {
  id: string;
  name: string;
  description: string;
  kind: "manual" | "weight";
  points: number;
  cutoffTime: string | null;
  isActive: boolean;
  availableFrom: string;
};
type AdminUser = {
  id: string;
  nickname: string;
  isAdmin: boolean;
  experience: Experience;
};
type AdminChallenge = {
  id: string;
  name: string;
  owner: string;
  startDate: string;
  endDate: string;
  participants: number;
  pendingInvitations: number;
};
export function Admin() {
  const [users, setUsers] = useState<AdminUser[]>([]);
  const [challenges, setChallenges] = useState<AdminChallenge[]>([]);
  const [activities, setActivities] = useState<Activity[]>([]);
  const [error, setError] = useState("");
  const [message, setMessage] = useState("");
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [editing, setEditing] = useState<Activity | null>(null);
  const [formVersion, setFormVersion] = useState(0);
  async function refresh() {
    const [u, c, a] = await Promise.all([
      api<AdminUser[]>("/api/admin/users"),
      api<AdminChallenge[]>("/api/admin/challenges"),
      api<Activity[]>("/api/admin/activities"),
    ]);
    setUsers(u);
    setChallenges(c);
    setActivities(a);
  }
  useEffect(() => {
    void refresh()
      .catch((e) => setError(e.message))
      .finally(() => setLoading(false));
  }, []);
  async function save(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const data = new FormData(e.currentTarget);
    setBusy(true);
    setError("");
    setMessage("");
    try {
      await api(
        editing
          ? `/api/admin/activities/${editing.id}`
          : "/api/admin/activities",
        editing ? "PUT" : "POST",
        {
          name: data.get("name"),
          description: data.get("description"),
          points: Number(data.get("points")),
          cutoffTime: data.get("cutoff") || null,
          isActive: data.get("active") === "on",
        },
      );
      await refresh();
      window.dispatchEvent(new Event("dod-activities-changed"));
      setEditing(null);
      setFormVersion((v) => v + 1);
      setMessage(
        "Activity saved. Previously reported days keep their original rules and points.",
      );
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  return (
    <section className="admin-panel">
      <div className="history-heading">
        <div>
          <p className="eyebrow">ADMINISTRATION</p>
          <h2>Users, challenges & activity rules</h2>
        </div>
        <button
          disabled={busy || loading}
          onClick={() => {
            setError("");
            void refresh().catch((e) => setError(e.message));
          }}
        >
          Refresh admin
        </button>
      </div>
      {loading && <p role="status">Loading administration…</p>}
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
      {message && <p role="status">{message}</p>}
      <section className="social-panel">
        <h3>Activity types</h3>
        <p>
          Add daily goals or edit their rewards. The weight activity is
          automatic; new activities are self-reported. Retiring an activity
          stops new reports but keeps earned XP and allows corrections to
          existing reports.
        </p>
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Activity</th>
                <th>Type</th>
                <th>XP</th>
                <th>Cutoff</th>
                <th>Status</th>
                <th>Action</th>
              </tr>
            </thead>
            <tbody>
              {activities.map((a) => (
                <tr key={a.id}>
                  <td>{a.name}</td>
                  <td>{a.kind === "weight" ? "Automatic" : "Daily report"}</td>
                  <td>{a.points}</td>
                  <td>{a.cutoffTime ?? "—"}</td>
                  <td>{a.isActive ? "Active" : "Retired"}</td>
                  <td>
                    <button
                      disabled={busy}
                      onClick={() => {
                        setEditing(a);
                        setFormVersion((v) => v + 1);
                      }}
                    >
                      Edit<span className="sr-only"> {a.name}</span>
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <form className="activity-editor" onSubmit={save} key={formVersion}>
          <h3>{editing ? `Edit ${editing.name}` : "Add an activity"}</h3>
          <label>
            Activity name
            <input
              name="name"
              defaultValue={editing?.name ?? ""}
              maxLength={80}
              required
            />
          </label>
          <label>
            Description / goal
            <textarea
              name="description"
              defaultValue={editing?.description ?? ""}
              maxLength={300}
              required
            />
          </label>
          <label>
            Experience points
            <input
              name="points"
              type="number"
              min={1}
              max={1000}
              step={1}
              defaultValue={editing?.points ?? 10}
              required
            />
          </label>
          {editing?.kind !== "weight" && (
            <label>
              Optional cutoff time
              <input
                name="cutoff"
                type="time"
                defaultValue={editing?.cutoffTime ?? ""}
              />
            </label>
          )}
          <label className="checkbox-label">
            <input
              name="active"
              type="checkbox"
              defaultChecked={editing?.isActive ?? true}
            />{" "}
            Active
          </label>
          <button disabled={busy}>
            {busy ? "Saving…" : editing ? "Save activity" : "Add activity"}
          </button>
          {editing && (
            <button
              type="button"
              disabled={busy}
              onClick={() => {
                setEditing(null);
                setFormVersion((v) => v + 1);
              }}
            >
              Cancel editing
            </button>
          )}
        </form>
      </section>
      <section className="social-panel">
        <h3>All users ({users.length})</h3>
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Nickname</th>
                <th>Role</th>
                <th>XP</th>
                <th>Level</th>
              </tr>
            </thead>
            <tbody>
              {users.map((u) => (
                <tr key={u.id}>
                  <td>{u.nickname}</td>
                  <td>{u.isAdmin ? "Admin" : "User"}</td>
                  <td>{u.experience.totalXp}</td>
                  <td>{u.experience.level}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>
      <section className="social-panel">
        <h3>All challenges ({challenges.length})</h3>
        {challenges.length === 0 ? (
          <p>No challenges yet.</p>
        ) : (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Name</th>
                  <th>Creator</th>
                  <th>Start</th>
                  <th>End (exclusive)</th>
                  <th>Participants</th>
                  <th>Pending invitations</th>
                </tr>
              </thead>
              <tbody>
                {challenges.map((c) => (
                  <tr key={c.id}>
                    <td>{c.name}</td>
                    <td>{c.owner}</td>
                    <td>{c.startDate}</td>
                    <td>{c.endDate}</td>
                    <td>{c.participants}</td>
                    <td>{c.pendingInvitations}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </section>
  );
}
