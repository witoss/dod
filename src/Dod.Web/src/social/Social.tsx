import { useEffect, useState, type FormEvent } from "react";
import { api } from "../api";
import { Avatar, type User } from "./Account";
type Friend = { user: User; status: "pending" | "accepted"; incoming: boolean };
type Challenge = {
  id: string;
  ownerId: string;
  name: string;
  startDate: string;
  endDate: string;
  status: "pending" | "accepted";
};
type Standing = User & {
  kgLost: number | null;
  measurements: number;
  baselineDate: string | null;
  latestDate: string | null;
  rank: number | null;
};
const utcToday = () => new Date().toISOString().slice(0, 10);
export function Social() {
  const [friends, setFriends] = useState<Friend[]>([]);
  const [challenges, setChallenges] = useState<Challenge[]>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const [standings, setStandings] = useState<Standing[]>([]);
  const [found, setFound] = useState<User | null>(null);
  const [error, setError] = useState("");
  const [message, setMessage] = useState("");
  const [busy, setBusy] = useState(false);
  const [loading, setLoading] = useState(true);
  async function refresh(id: string | null = selected) {
    const [f, c] = await Promise.all([
      api<Friend[]>("/api/social/friends"),
      api<Challenge[]>("/api/social/challenges"),
    ]);
    setFriends(f);
    setChallenges(c);
    if (id && c.some((ch) => ch.id === id && ch.status === "accepted")) {
      setStandings(
        await api<Standing[]>(`/api/social/challenges/${id}/leaderboard`),
      );
    } else {
      setSelected(null);
      setStandings([]);
    }
  }
  useEffect(() => {
    void refresh(null)
      .catch((e) => setError(e.message))
      .finally(() => setLoading(false));
  }, []);
  async function run(action: () => Promise<unknown>, success = "Saved.") {
    setBusy(true);
    setError("");
    setMessage("");
    try {
      await action();
      await refresh();
      setMessage(success);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  async function search(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const nickname = String(new FormData(e.currentTarget).get("nickname"));
    setFound(null);
    await run(
      async () =>
        setFound(
          await api<User>(
            `/api/social/users?nickname=${encodeURIComponent(nickname)}`,
          ),
        ),
      "Search complete.",
    );
  }
  async function create(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const data = new FormData(e.currentTarget);
    await run(
      () =>
        api("/api/social/challenges", "POST", {
          name: data.get("name"),
          startDate: data.get("start"),
          weeks: Number(data.get("weeks")),
        }),
      "Challenge created. Open it to invite friends.",
    );
  }
  async function open(id: string) {
    setBusy(true);
    setError("");
    setSelected(null);
    setStandings([]);
    try {
      const rows = await api<Standing[]>(
        `/api/social/challenges/${id}/leaderboard`,
      );
      setStandings(rows);
      setSelected(id);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  const active = challenges.find((c) => c.id === selected);
  const accepted = friends.filter((f) => f.status === "accepted");
  function friendList(title: string, list: Friend[]) {
    return (
      <section>
        <h3>
          {title} ({list.length})
        </h3>
        {list.length === 0 ? (
          <p className="hint">None yet.</p>
        ) : (
          <ul className="people-list">
            {list.map((f) => (
              <li key={f.user.id}>
                <Avatar user={f.user} />
                <strong>{f.user.nickname}</strong>
                <div className="row-actions">
                  {f.status === "pending" && f.incoming ? (
                    <>
                      <button
                        disabled={busy}
                        onClick={() =>
                          void run(
                            () =>
                              api(`/api/social/friends/${f.user.id}`, "PUT", {
                                accept: true,
                              }),
                            "Friend request accepted.",
                          )
                        }
                      >
                        Accept
                      </button>
                      <button
                        disabled={busy}
                        onClick={() =>
                          void run(
                            () =>
                              api(`/api/social/friends/${f.user.id}`, "PUT", {
                                accept: false,
                              }),
                            "Request declined.",
                          )
                        }
                      >
                        Decline
                      </button>
                    </>
                  ) : (
                    <button
                      disabled={busy}
                      onClick={() =>
                        void run(
                          () =>
                            api(`/api/social/friends/${f.user.id}`, "DELETE"),
                          "Removed.",
                        )
                      }
                    >
                      {f.status === "accepted"
                        ? "Remove friend"
                        : "Cancel request"}
                    </button>
                  )}
                </div>
              </li>
            ))}
          </ul>
        )}
      </section>
    );
  }
  return (
    <div className="social-space">
      <div className="history-heading">
        <h2>Friends & challenges</h2>
        <button
          disabled={busy || loading}
          onClick={() => void run(() => Promise.resolve(), "Updated.")}
        >
          Refresh
        </button>
      </div>
      {loading && <p role="status">Loading friends and challenges…</p>}
      {error && (
        <p role="alert" className="error">
          {error}
        </p>
      )}
      {message && <p role="status">{message}</p>}
      <section className="social-panel">
        <h2>Find a friend</h2>
        <p>Enter their exact nickname. There is no public user directory.</p>
        <form onSubmit={search}>
          <label>
            Exact nickname
            <input
              name="nickname"
              required
              pattern="[a-zA-Z0-9_]{3,24}"
              maxLength={24}
              onChange={() => setFound(null)}
            />
          </label>
          <button disabled={busy}>Search</button>
        </form>
        {found && (
          <div className="person-result">
            <Avatar user={found} />
            <strong>{found.nickname}</strong>
            <button
              disabled={busy || friends.some((f) => f.user.id === found.id)}
              onClick={() =>
                void run(
                  () =>
                    api("/api/social/friends", "POST", {
                      nickname: found.nickname,
                    }),
                  "Friend request sent.",
                )
              }
            >
              Send friend request
            </button>
          </div>
        )}
        {friendList(
          "Pending requests received",
          friends.filter((f) => f.status === "pending" && f.incoming),
        )}
        {friendList(
          "Requests sent",
          friends.filter((f) => f.status === "pending" && !f.incoming),
        )}
        {friendList("Your friends", accepted)}
      </section>
      <section className="social-panel">
        <h2>Create a weight-loss challenge</h2>
        <p>
          Compare kilograms lost over a set number of weeks. Choose a future
          start date to give friends time to join.
        </p>
        <form onSubmit={create}>
          <label>
            Challenge name
            <input name="name" required maxLength={80} />
          </label>
          <label>
            Start date (UTC)
            <input
              name="start"
              type="date"
              min={utcToday()}
              defaultValue={utcToday()}
              required
            />
          </label>
          <label>
            Duration in weeks
            <input
              name="weeks"
              type="number"
              min={1}
              max={52}
              defaultValue={4}
              required
            />
          </label>
          <button disabled={busy}>Create challenge</button>
        </form>
      </section>
      <section className="social-panel">
        <h2>Your challenges</h2>
        {challenges.length === 0 && (
          <p>No challenges yet. Create one or ask a friend to invite you.</p>
        )}
        <ul className="challenge-list">
          {challenges.map((c) => (
            <li key={c.id}>
              <h3>{c.name}</h3>
              <p>
                {c.startDate} until {c.endDate} (end exclusive, UTC) ·{" "}
                {utcToday() < c.startDate
                  ? "Upcoming"
                  : utcToday() >= c.endDate
                    ? "Finished"
                    : "Active"}
              </p>
              {c.status === "pending" ? (
                <>
                  <p>
                    Accepting shares your challenge progress with all
                    participants.
                  </p>
                  <button
                    disabled={busy || utcToday() > c.startDate}
                    onClick={() =>
                      void run(
                        () =>
                          api(
                            `/api/social/challenges/${c.id}/invitation`,
                            "PUT",
                            { accept: true },
                          ),
                        "Challenge joined.",
                      )
                    }
                  >
                    Accept challenge
                  </button>
                  <button
                    disabled={busy}
                    onClick={() =>
                      void run(
                        () =>
                          api(
                            `/api/social/challenges/${c.id}/invitation`,
                            "PUT",
                            { accept: false },
                          ),
                        "Invitation declined.",
                      )
                    }
                  >
                    Decline
                  </button>
                </>
              ) : (
                <>
                  <button disabled={busy} onClick={() => void open(c.id)}>
                    Leaderboard & invitations
                  </button>
                  <button
                    disabled={busy}
                    onClick={() => {
                      if (
                        window.confirm(
                          "Leave this challenge and stop sharing your progress with its participants?",
                        )
                      )
                        void run(
                          () =>
                            api(
                              `/api/social/challenges/${c.id}/participation`,
                              "DELETE",
                            ),
                          "Challenge left.",
                        );
                    }}
                  >
                    Leave challenge
                  </button>
                </>
              )}
            </li>
          ))}
        </ul>
      </section>
      {active && (
        <section className="social-panel">
          <h2>{active.name} · leaderboard</h2>
          <p>
            First minus latest weight inside the challenge dates. Two
            measurements are needed for a rank. Ties share a rank; negative
            values mean weight gained. Raw weights and calories stay private.
          </p>
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Rank</th>
                  <th>Participant</th>
                  <th>kg lost</th>
                  <th>Measurements</th>
                  <th>Measurement dates</th>
                </tr>
              </thead>
              <tbody>
                {standings.map((s) => (
                  <tr key={s.id}>
                    <td>{s.rank ?? "—"}</td>
                    <td>
                      <Avatar user={s} /> {s.nickname}
                    </td>
                    <td>
                      {s.kgLost === null
                        ? "Awaiting measurements"
                        : s.kgLost.toFixed(2)}
                    </td>
                    <td>{s.measurements}</td>
                    <td>
                      {s.baselineDate
                        ? `${s.baselineDate} → ${s.latestDate}`
                        : "—"}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <p className="hint">
            Progress is recalculated from saved entries, including corrections.
            Different baseline dates may affect comparisons. Removing a friend
            does not remove challenge membership; leave the challenge to stop
            sharing there.
          </p>
          {utcToday() <= active.startDate && (
            <form
              onSubmit={(e) => {
                e.preventDefault();
                const userId = new FormData(e.currentTarget).get("friend");
                void run(
                  () =>
                    api(`/api/social/challenges/${active.id}/invite`, "POST", {
                      userId,
                    }),
                  "Challenge invitation sent.",
                );
              }}
            >
              <label>
                Invite an accepted friend
                <select name="friend" required>
                  <option value="">Choose a friend</option>
                  {accepted
                    .filter((f) => !standings.some((s) => s.id === f.user.id))
                    .map((f) => (
                      <option key={f.user.id} value={f.user.id}>
                        {f.user.nickname}
                      </option>
                    ))}
                </select>
              </label>
              <button disabled={busy || accepted.length === 0}>
                Invite to challenge
              </button>
            </form>
          )}
        </section>
      )}
    </div>
  );
}
