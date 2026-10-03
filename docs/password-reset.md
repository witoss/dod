# Password recovery

On the sign-in page, choose **Forgot password?** and enter your nickname. A recovery link is queued only if that account already has a saved, confirmed email address in Profile. Weekly summaries can be turned off. Unknown accounts, unverified addresses, unavailable sending, and requests during cooldown receive the same generic response. There is no recovery through an unconfirmed address or the original tracker password.

The email link opens a signed-out form to enter and confirm a new password of 12–128 characters. Opening the link alone makes no changes. Links expire after **30 minutes**, work once, and are superseded by a later request. Requests have a one-minute per-account cooldown plus the existing account-operation rate limit. Changing the saved email address invalidates recovery links. The account's password and session state are unchanged until a valid reset is completed.

After reset, sign in normally with the new password. All existing sessions for that account are revoked, including cookies created before this feature. Other accounts' sessions are unaffected. A password-changed notification is queued to the confirmed address; it contains no password.

## Implementation

The public routes are `POST /api/account/forgot-password` with `{ "nickname": "Alice" }` and `POST /api/account/reset-password` with `{ "token": "…", "password": "…" }`. Both require CSRF protection and account rate limiting. Recovery requests return a generic message with a common minimum response duration and use asynchronous queue delivery.

Tokens contain 256 bits of cryptographic randomness and only their SHA-256 hashes are stored in recovery records. Reset links use URL fragments, removed from browser history before rendering, so tokens are not sent in HTTP URLs or referrers. Queued message contents use the existing Data Protection encryption. Password hashing, token consumption, session-version increment and notification enqueue happen in one SQLite transaction, so concurrent uses of a link can succeed only once. Cookies carry a session version checked against the account on authenticated requests; older cookies represent version zero.

The existing SMTP or Cloudflare HTTPS transport delivers recovery mail. No extra provider credentials or Worker deployment are required. Queued recovery messages are cancelled when expired, consumed, superseded or associated with an old/unverified email address. Recovery logs include internal user IDs and operation status, without addresses, tokens or passwords.

These choices follow the [OWASP password recovery guidance](https://cheatsheetseries.owasp.org/cheatsheets/Forgot_Password_Cheat_Sheet.html).

## Deployment and checks

Database migration **7** adds account session versions and recovery records without removing journal data, XP, passwords or email confirmations. Keep a database and Data Protection key backup before deploying; older app versions reject schema 7 and require a compatible backup for rollback. Preserve `/data` as usual. Recovery links expire independently of SMTP/API queue retries, so a provider outage lasting 30 minutes requires requesting a new link.

After deploying, verify with your own account:

1. Save and confirm an email address in Profile.
2. Sign out, choose **Forgot password?**, and request a link using your nickname.
3. Receive the link, open it, and explicitly save matching new passwords.
4. Check that the old password fails, the new password works, an already-open session must sign in again, and the link cannot be reused.
5. Check the password-changed notification and provider delivery logs.

Automated tests cover eligibility, generic responses, cooldown, CSRF, password bounds, token purpose, expiry, single use and concurrent consumption, email changes, queue cancellation, session revocation, legacy cookies, migration and restart, plus signed-out UI interactions. Live delivery still needs the operator check above.
