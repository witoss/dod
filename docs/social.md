# Accounts, friends, and challenges

## Upgrade an existing journal

This release replaces the shared Basic login with individual accounts. On startup, schema migration 4 reserves every existing entry and the calorie reference for the original owner. It does **not** give that journal to the first person who registers.

1. Let the deployment script create its pre-update database backup. Keep it: schema 4 cannot be opened by the older single-user app.
2. Open the HTTPS website and select **Claim existing journal**.
3. Choose a unique nickname and a new account password (12–128 characters).
4. Enter the existing server `TRACKER_PASSWORD` to prove ownership. Use the server's value, which may differ from your Mac's.
5. Verify your entries and calorie reference. Sign out and sign back in with the new nickname/password.

The claim can succeed only once. Friends select **Create account** and get empty, private journals. They do not need the tracker password. Keep `TRACKER_PASSWORD` in the server configuration for compatibility with the deployment Compose file; after claiming, it cannot reset or reopen the owner's account.

There is no email verification, password-reset flow, account deletion, or MFA in this release. Store account passwords in a password manager. Account recovery is a future feature, not a hidden use of the old tracker password.

## Friendships

Nicknames contain 3–24 ASCII letters, digits, or underscores and are unique ignoring case: `Alice` and `alice` identify the same account. **Find a friend** accepts a complete nickname; partial searches and a public directory are not available.

Search for a nickname, inspect the returned nickname/avatar, and send a request. The recipient sees it under **Pending requests received** and may accept or decline. Senders see **Requests sent** and can cancel. Only accepted requests create friendships. Duplicate requests, crossed requests, and self-invitations are rejected. Either person can remove a friendship.

Nickname changes preserve friendships because relationships use an internal user ID. Nicknames and avatars are visible to signed-in users who know the exact nickname and to challenge participants; journals and calorie settings remain private.

## Challenges and scoring

Any signed-in user can create a named weight-loss challenge, choose a start date, and set 1–52 weeks. Start dates range from today to one year ahead. Challenge boundaries use UTC calendar dates; the journal still records the date you explicitly select.

The creator joins automatically. Every accepted participant may invite **their own accepted friends**. An invitation must be accepted separately before the recipient participates or can read the leaderboard. Acceptance means sharing daily weights within the challenge dates and calculated progress with all challenge participants, including people who are not direct friends.

Invitations and acceptance are allowed through the start date; they close the next UTC day. Choose a future start date to allow time to join. Removing a friendship revokes pending invitations between those users. Existing challenge membership remains independent of friendship; use **Leave challenge** to remove your progress and access. Leaving also cancels pending invitations you sent for that challenge. There is no delete/edit-challenge feature yet.

A one-week challenge starting October 2 includes October 2–8; October 9 is the exclusive end. For each accepted participant, the leaderboard:

1. Takes only weights inside that date range and no later than today's UTC date.
2. Uses the earliest eligible weight as the baseline and the latest as the current weight.
3. Computes **kilograms lost = baseline − latest weight**, rounded to two decimal places.
4. Sorts descending; equal losses share a rank (1, 1, 3). Gains have negative values.
5. Leaves users with fewer than two measurements unranked. It displays measurement counts and baseline/latest dates so different reporting histories are visible.

Weights before the start and on/after the exclusive end do not count. Missing days are not invented or interpolated. The API computes standings from the journal whenever requested. Open a leaderboard or use **Refresh** to fetch current results. Corrections to entries inside the challenge window recalculate even finished challenges; there is no frozen result or anti-cheating audit in this release.

The challenge detail includes a **Daily participant weights** chart with one line per accepted participant. It shares actual weights only within the challenge window, through today. The journal stores one weight per user per date: saving again replaces that day's weight, so the chart always shows the latest saved value for that date. Missing days are gaps, not estimated weights. Use the legend to hide/show lines, tap or focus points for details, or open the accessible daily-weight table. Pending invitees and outsiders cannot access the chart, and leaving removes a participant's data and access. Calories and weights outside the challenge dates remain private.

This applies to existing challenges too: actual daily weights are now visible to their accepted participants. The creation/invitation text explains this sharing. A leaderboard describes recorded changes, not a recommended weight-loss pace.

## Profile and avatar

Open **Profile** to change your nickname, upload an avatar, or remove it. Supported avatars are PNG/JPEG, at most 1 MB and 2048 × 2048 pixels. The server bounds upload bytes and checks raster headers/dimensions instead of trusting the filename. SVG is not accepted. It does not fully decode or re-encode image pixels or strip metadata; use an image you are comfortable sharing. A corrupt image can still fail to render.

Images are stored in SQLite, so database backups include them. They are served only to signed-in users with a fixed image content type, `nosniff`, and no-store caching. Uploaded filenames are never used as disk paths.

## Implementation decisions

- ASP.NET Core cookie authentication identifies the user. ASP.NET Core Identity's `PasswordHasher` stores salted password hashes, not plaintext. This uses framework cookie authentication with a small SQLite account store, not the full Identity user-management system. See [Microsoft's cookie authentication guide](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/cookie?view=aspnetcore-10.0).
- Sessions last seven days without sliding renewal and are HttpOnly/SameSite Strict. They require Secure cookies in production. Local loopback Compose explicitly allows HTTP cookies via `Authentication__AllowHttp=true`; do not add that setting on the public VPS.
- Behind Caddy, the app must process the original HTTPS scheme from its explicitly trusted proxy. The VPS Compose file configures a fixed proxy IP on a dedicated Docker network. See the [existing-deployment fix](vps.md#https-session-token-fix-for-existing-deployments) if claiming fails with a session-verification error.
- Every modifying API request needs the antiforgery header from `/api/account/csrf`, including login and registration. The frontend refreshes that token after changing accounts. CORS is not enabled.
- Account operations and exact-nickname lookup share a process-wide limit of 30 requests/minute. This gives the small app a basic throttle without trusting client-supplied proxy headers. It is not per-account lockout, a distributed rate limiter, or a complete abuse-prevention system; reassess as usage grows.
- Entries use `(UserId, Date)` as their primary key, and references belong to a user. Request handlers derive ownership from the authenticated session; clients cannot choose a journal owner.
- Friendship pairs are unique in either direction. Challenge invitations check accepted friendship and participant membership in SQL. Pending invitees and outsiders cannot read standings.
- Data-protection keys live alongside SQLite (`/data/keys` in Docker), so sessions survive container replacement. Protect that volume and its backups: it contains personal data, password hashes, and session-encryption keys.
- Schema migration 4 runs transactionally. Existing original/v2/v3 data is preserved under a reserved owner. Do not roll back to a v3 image against a v4 database; restore a deliberate pre-upgrade backup with the matching older image if necessary.

The original owner is also the administrator after migration 5; see [daily activities and administration](motivation.md).

The server remains a single app instance with SQLite. No new paid service or database server is required.

## API additions

All social/profile routes require a signed-in session. Mutations also require `X-CSRF-TOKEN`.

| Route | Purpose |
| --- | --- |
| `GET /api/account/csrf` | Obtain an antiforgery token and cookie |
| `GET /api/account/me` | Current account or 401 |
| `POST /api/account/register` | New private account: nickname, password |
| `POST /api/account/claim` | Claim reserved journal: nickname, password, ownerPassword |
| `POST /api/account/login`, `/logout` | Begin/end session |
| `PUT /api/account/profile` | Update nickname |
| `PUT /api/account/avatar` | Upload raw PNG/JPEG bytes |
| `DELETE /api/account/avatar` | Remove own avatar |
| `GET /api/avatars/{id}` | Read an account avatar |
| `GET /api/social/users?nickname=Alice` | Exact nickname lookup |
| `GET /api/social/friends` | Friends plus incoming/outgoing requests |
| `POST /api/social/friends` | Request friendship by nickname |
| `PUT /api/social/friends/{id}` | Recipient accepts/declines with `{ "accept": true/false }` |
| `DELETE /api/social/friends/{id}` | Remove friendship or cancel request |
| `GET /api/social/challenges` | Own joined/invited challenges |
| `POST /api/social/challenges` | Create with name, startDate, weeks |
| `POST /api/social/challenges/{id}/invite` | Invite own accepted friend by userId |
| `PUT /api/social/challenges/{id}/invitation` | Accept/decline invitation |
| `DELETE /api/social/challenges/{id}/participation` | Leave challenge |
| `GET /api/social/challenges/{id}/leaderboard` | Participant-only calculated standings |
| `GET /api/social/challenges/{id}/weights` | Participant-only daily weight series inside challenge dates |
