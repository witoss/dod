# Daily activities, experience, and administration

## Daily check-in

Activities belong to your personal journal and do not require a challenge or friends. Select the journal date, then use **Daily activities**:

- **No sweets:** choose **Goal met** if you avoided sweets for the whole day, or **Not this day** if you ate sweets. The initial reward is 10 XP.
- **No food after cutoff:** report whether you avoided eating after the displayed time. Initially this is 20:00 and earns 10 XP. The cutoff refers to your own local clock; it is self-reported, not detected by the app.
- **Weigh yourself:** saving a weight automatically earns 10 XP, once per journal date. You cannot manually check this activity off.

Admins may add other self-reported goals, choose their rewards and optional cutoff times, and retire activities. Complete manual reports after you know the result for the whole day; reporting a missed goal gives zero points. The app does not infer sweets or meal times from calorie entries.

XP uses UTC calendar dates for eligibility. Future-dated reports cannot earn XP, and activities cannot be reported for dates before they were introduced. The journal's existing date picker still starts with your browser's local date; near midnight it can be ahead of the current UTC date. In that case, wait until that date starts in UTC before earning XP. This can be revisited when per-user time zones are added.

The diary can still store future weight entries, but they do not award XP until an eligible date and a subsequent save. Backdated weights before the automatic activity's introduction earn zero XP. Saving again for an eligible date updates the weight, not the reward. Multiple saves and concurrent requests cannot produce multiple rewards for the same activity/date.

## Experience and levels

The top of the app shows total XP, the current level, and progress toward the next level:

| Total XP | Level | XP remaining until next level |
| --- | --- | --- |
| 0 | 1 | 100 |
| 90 | 1 | 10 |
| 100 | 2 | 100 |
| 250 | 3 | 50 |

The initial rule is **level = floor(total XP / 100) + 1**. A successful new reward shows **“Yoohoo! You gained X XP!”**. Crossing a boundary adds a level-up message. The popup can be dismissed and otherwise closes after six seconds. Refreshing the page does not replay rewards.

Levels currently recognize progress only. No feature is locked or unlocked yet, since the unlockable features have not been chosen. Future feature checks should use server-calculated experience and explicit unlock rules, not a browser-provided level.

A correction updates the saved result rather than adding another event. Changing **Goal met** to **Not this day** removes that day's XP and can lower your level. Changing it back restores the original amount, without increasing the maximum reward for that date. The popup describes negative adjustments separately.

## Admin panel

The original journal owner (the account with internal ID `legacy`, claimed using the original tracker secret) is the administrator. Existing owner sessions gain the Admin tab when the account is reloaded after deployment. Registering another account, changing a nickname, or sending an `isAdmin` field cannot grant this role. An unclaimed reserved owner has no session and cannot administer the app until properly claimed.

Select **Admin** to:

1. View all registered users with their role, XP, and level.
2. View all challenges, their creators, dates, accepted participant counts, and pending invitation counts.
3. Add activity types with a name, goal description, 1–1000 XP, optional cutoff time, and active status.
4. Edit or retire existing types, including changing the automatic weigh-in reward from its initial 10 XP.

Only one automatic weight activity exists; newly created types are manual reports. Retiring an activity stops new manual reports. Existing reports remain visible and can be corrected. A weight saved while its automatic activity is retired receives zero XP for that date; re-enabling the activity does not award points again for that already-recorded date.

For each first report, the server snapshots its name, description, cutoff time, and reward. Later admin edits affect newly recorded reports, not existing ones. Even a first **Not this day** report locks in that day's rule so later corrections remain consistent. This avoids silently rewriting past goals or changing earned XP across the app.

Admin lists intentionally do not include passwords, password hashes, raw journals, or other users' daily activity answers. Admin read and write permissions are checked against the database on every request. There is no UI for promoting more admins in this version.

## Upgrade and storage

Migration **5** adds `Users.IsAdmin`, `Activities`, and `ActivityReports`, without changing or deleting journal data, challenges, or friendships. Existing weights are marked as already recorded with zero XP: there is no retroactive reward. The original owner retains their current nickname and password; no second claim is necessary.

`ActivityReports` has a unique `(UserId, ActivityId, Date)` key. XP is the sum of completed reports' stored point values. Weigh-in storage and its reward happen in one SQLite transaction. Manual updates also use a transaction so reported XP changes correspond to the saved result. The client cannot choose reward amounts or XP totals.

Keep the deployment's pre-update backup. Older binaries reject a schema-5 database; rolling back to an older schema requires an intentional backup restore. No VPS Compose or secret changes are needed for this feature. Commit all new files as well as modified files, then push to run CI and deployment.

## API

All routes require a signed-in session. Mutations also require the existing antiforgery token.

| Route | Purpose |
| --- | --- |
| `GET /api/motivation/experience` | Current user's authoritative XP and level |
| `GET /api/motivation/{date}` | Daily goals, saved answers, reward values, and XP |
| `PUT /api/motivation/{date}/activities/{id}` | Report `{ "completed": true/false }` for a manual activity |
| `GET /api/admin/users` | Admin-only user list and experience |
| `GET /api/admin/challenges` | Admin-only challenge overview |
| `GET /api/admin/activities` | Admin-only activity definitions |
| `POST /api/admin/activities` | Create a manual activity |
| `PUT /api/admin/activities/{id}` | Edit or retire an activity |

Weight saves keep the existing `204 No Content` API response. Reward-producing endpoints include `X-XP-Change`, `X-XP-Total`, and `X-XP-Level-Up` headers. These drive the notification and refresh the authoritative XP display. Zero-change responses refresh the daily status without replaying a congratulation. Manual reports additionally return the change and experience as JSON.
