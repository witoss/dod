import { useEffect, useRef, useState } from "react";
import { api } from "../api";
import "./motivation.css";
export type Experience = {
  totalXp: number;
  level: number;
  xpIntoLevel: number;
  xpPerLevel: number;
  xpToNextLevel: number;
};
type DailyActivity = {
  id: string;
  name: string;
  description: string;
  kind: "manual" | "weight";
  points: number;
  cutoffTime: string | null;
  isActive: boolean;
  completed: boolean | null;
  earned: number;
  canReport: boolean;
};
type MotivationDay = {
  date: string;
  today: string;
  experience: Experience;
  activities: DailyActivity[];
};
type XpEvent = { awarded: number; totalXp: number; leveledUp: boolean };
export function ExperienceBar() {
  const [experience, setExperience] = useState<Experience | null>(null);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const sequence = useRef(0);
  async function refresh() {
    const request = ++sequence.current;
    try {
      const result = await api<Experience>("/api/motivation/experience");
      if (request === sequence.current) {
        setExperience(result);
        setError("");
      }
    } catch (e) {
      if (request === sequence.current) setError((e as Error).message);
    }
  }
  useEffect(() => {
    void refresh();
    const changed = (event: Event) => {
      const detail = (event as CustomEvent<XpEvent>).detail;
      void refresh();
      if (detail.awarded > 0) {
        setNotice(
          `Yoohoo! You gained ${detail.awarded} XP!${detail.leveledUp ? ` Level up — you reached level ${Math.floor(detail.totalXp / 100) + 1}!` : ""}`,
        );
        clearTimeout(timer.current);
        timer.current = setTimeout(() => setNotice(""), 6000);
      } else if (detail.awarded < 0) {
        setNotice(
          `Report corrected. Your experience was adjusted by ${detail.awarded} XP.`,
        );
        clearTimeout(timer.current);
        timer.current = setTimeout(() => setNotice(""), 6000);
      }
    };
    const focus = () => void refresh();
    window.addEventListener("dod-xp-change", changed);
    window.addEventListener("focus", focus);
    return () => {
      ++sequence.current;
      clearTimeout(timer.current);
      window.removeEventListener("dod-xp-change", changed);
      window.removeEventListener("focus", focus);
    };
  }, []);
  return (
    <>
      <section className="experience-bar" aria-label="Your experience">
        {experience ? (
          <>
            <div>
              <span className="eyebrow">YOUR EXPERIENCE</span>
              <h2>
                Level {experience.level} <span>· {experience.totalXp} XP</span>
              </h2>
            </div>
            <div className="experience-progress">
              <label htmlFor="experience-progress">
                {experience.xpToNextLevel} XP to level {experience.level + 1}
              </label>
              <progress
                id="experience-progress"
                max={experience.xpPerLevel}
                value={experience.xpIntoLevel}
              />
              <small>Every 100 XP brings a new level.</small>
            </div>
          </>
        ) : (
          !error && <p role="status">Loading experience…</p>
        )}
        {error && (
          <p className="error" role="alert">
            Experience could not load.{" "}
            <button onClick={() => void refresh()}>Retry XP</button>
          </p>
        )}
      </section>
      {notice && (
        <aside className="xp-toast" role="status" aria-live="polite">
          <span>{notice}</span>
          <button
            aria-label="Dismiss notification"
            onClick={() => setNotice("")}
          >
            ×
          </button>
        </aside>
      )}
    </>
  );
}
export function DailyActivities({ date }: { date: string }) {
  const [day, setDay] = useState<MotivationDay | null>(null);
  const [error, setError] = useState("");
  const [message, setMessage] = useState("");
  const [busy, setBusy] = useState(false);
  const [revision, setRevision] = useState(0);
  useEffect(() => {
    const changed = () => setRevision((value) => value + 1);
    window.addEventListener("dod-xp-change", changed);
    window.addEventListener("dod-activities-changed", changed);
    return () => {
      window.removeEventListener("dod-xp-change", changed);
      window.removeEventListener("dod-activities-changed", changed);
    };
  }, []);
  useEffect(() => {
    let active = true;
    setError("");
    api<MotivationDay>(`/api/motivation/${date}`)
      .then((result) => {
        if (active) setDay(result);
      })
      .catch((e) => {
        if (active) setError(e.message);
      });
    return () => {
      active = false;
    };
  }, [date, revision]);
  async function report(id: string, completed: boolean) {
    setBusy(true);
    setError("");
    setMessage("");
    try {
      await api(`/api/motivation/${date}/activities/${id}`, "PUT", {
        completed,
      });
      setMessage("Daily report saved.");
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  const current = day?.date === date ? day : null;
  return (
    <section
      className="daily-activities social-panel"
      aria-label="Daily motivation activities"
    >
      <div className="history-heading">
        <div>
          <p className="eyebrow">BUILD YOUR DAILY HABITS</p>
          <h2>Daily activities · {date}</h2>
        </div>
        {current && (
          <strong>
            {current.activities.reduce((sum, a) => sum + a.earned, 0)} XP for
            this date
          </strong>
        )}
      </div>
      <p>
        Report whether you met each goal for the whole day. These activities are
        personal and independent of challenges. Cutoff times refer to your local
        clock; XP uses UTC calendar dates; future dates cannot earn points.
      </p>
      {error && (
        <p className="error" role="alert">
          {error}{" "}
          <button onClick={() => setRevision((r) => r + 1)}>
            Retry activities
          </button>
        </p>
      )}
      {message && <p role="status">{message}</p>}
      {!current && !error && <p role="status">Loading daily activities…</p>}
      {current && current.activities.length === 0 && (
        <p>No activities are available for this date.</p>
      )}
      {current && date > current.today && (
        <p>Future dates cannot earn XP. Report on or after this date (UTC).</p>
      )}
      <div className="activity-grid">
        {current?.activities.map((activity) => (
          <article className="activity-card" key={activity.id}>
            <div className="activity-heading">
              <h3>{activity.name}</h3>
              <span className="xp-pill">{activity.points} XP</span>
            </div>
            <p>{activity.description}</p>
            {activity.cutoffTime && (
              <p className="activity-cutoff">
                Cutoff: <strong>{activity.cutoffTime}</strong> (your local time)
              </p>
            )}
            {activity.kind === "weight" ? (
              <p className="activity-state">
                {activity.completed
                  ? `Recorded · ${activity.earned} XP earned`
                  : "Automatic · save your weight above to complete this activity."}
              </p>
            ) : (
              <>
                <p className="activity-state">
                  {activity.completed === null
                    ? "Not reported"
                    : activity.completed
                      ? `Goal met · ${activity.earned} XP earned`
                      : "Goal not met · 0 XP"}
                  {!activity.isActive ? " · retired activity" : ""}
                </p>
                <div className="activity-actions">
                  <button
                    disabled={busy || !activity.canReport}
                    aria-pressed={activity.completed === true}
                    onClick={() => void report(activity.id, true)}
                  >
                    Goal met
                  </button>
                  <button
                    disabled={busy || !activity.canReport}
                    aria-pressed={activity.completed === false}
                    onClick={() => void report(activity.id, false)}
                  >
                    Not this day
                  </button>
                </div>
              </>
            )}
          </article>
        ))}
      </div>
      <p className="hint">
        One reward per activity per date. Corrections adjust your XP; repeated
        saves do not add rewards. Points and wording are kept as they were when
        first reported.
      </p>
    </section>
  );
}
