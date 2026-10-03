# Weekly summaries and email delivery

In Profile, save your email address and choose **Send me weekly summaries**. A confirmation email is queued when you save a new address and delivery is configured. Open the link and press **Confirm email**; no login is required. Confirmation links expire in 24 hours and work once. **Resend confirmation** is limited to once per minute. Changing the address resets verification and invalidates old links. An unverified address never receives a recap.

Turn summaries off in Profile or open **Stop weekly emails** in a recap and press **Unsubscribe**. Link visits alone do not change preferences. The address is private and is never included in social or admin account responses. Email confirmation is separate from nickname/password login; it does not provide password recovery.

## Week boundaries and XP

Weeks run Monday through Sunday in UTC. Existing accounts become eligible on the Monday after the database upgrade; newly registered accounts become eligible on the Monday after registration. There are no bonuses for earlier weeks or partial onboarding weeks.

The weekly activity roster is fixed at Monday. Admin additions, name edits, activation and retirement affect the next Monday's roster. Daily point and description changes still follow the existing daily activity rules. A retired goal remains reportable for weeks where it belonged to the roster; saved reports keep their original XP. Extra activities can earn daily XP without entering that week's roster.

Every goal in the roster must be completed on all seven days for a 50 XP bonus. An empty roster earns no bonus. Missing and negative reports do not qualify. Weight goals complete when weight is recorded. Bonuses are computed for all accounts, even without email opt-in or SMTP. Correcting a closed week's report can add or remove its bonus; experience totals and admin totals include bonuses. The worker checks closed weeks after startup and each minute.

Recaps are queued from Monday **08:00 UTC**, normally within one minute. Only the latest closed eligible week is queued, at most once per account/week. Restarting catches up that recap during the following week without sending older backlogs. Enabling or confirming midweek can therefore queue the previous week's recap. Failed or cancelled recaps are not recreated for the same week.

Weight change compares the first and last measurements inside the week; two measurements are required. Challenge scores compare accepted participants' first and last measurements from challenge start through Sunday, respecting the challenge's exclusive end date. Invitations show their status without revealing standings. Future measurements do not affect rankings. Recaps are snapshots at preparation time; corrections appear in the app and do not send replacement emails.

## Email API

All mutations use the existing antiforgery token. Preference routes require a signed-in account; link actions use the protected token and work signed out. Responses and errors are never cached.

| Route | Purpose |
| --- | --- |
| `GET /api/email/preferences` | Own email, enabled/verified flags and sending availability |
| `PUT /api/email/preferences` | Save `{ "email": "you@example.com", "enabled": true }` |
| `POST /api/email/resend` | Queue confirmation for the saved address |
| `POST /api/email/verify` | Confirm `{ "token": "…" }` |
| `POST /api/email/unsubscribe` | Disable summaries using `{ "token": "…" }` |
| `GET /api/motivation/weekly/{date}` | Own recap, normalizing date to Monday |

## Production SMTP setup

Use a transactional SMTP provider and a sender address/domain that you control. Configure SPF, DKIM and DMARC using that provider's DNS instructions; check alignment with the From domain. See [Google's sender guidelines](https://support.google.com/mail/answer/81126?hl=en). The application sends multipart HTML/plain-text email with an unsubscribe link in the body. It does not implement provider bounce processing or RFC 8058 one-click unsubscribe headers.

The production Compose file forwards these variables to ASP.NET configuration. Add values privately to the VPS configuration file used by the existing deployment scripts. Keep that file mode `600`, outside Git. Do not paste credentials into chat, logs, commands, or this document. Do not `source` the file or print rendered Compose configuration containing passwords.

```dotenv
EMAIL_ENABLED=true
EMAIL_PUBLIC_URL=https://your-journal.example
EMAIL_SMTP_HOST=smtp.your-provider.example
EMAIL_SMTP_PORT=587
EMAIL_SMTP_SECURITY=StartTls
EMAIL_FROM='dodo <summaries@your-domain.example>'
EMAIL_SMTP_USERNAME=your-provider-username
EMAIL_SMTP_PASSWORD='replace-privately-with-your-provider-secret'
```

`EMAIL_PUBLIC_URL` is the public HTTPS origin, without a trailing slash, query, or fragment. It must match the journal people open. Use the provider's submission port: normally `587` with `StartTls`, or `465` with `SslOnConnect`. Production never allows plaintext SMTP or a certificate validation bypass. [MailKit documents its TLS connection options](https://mimekit.net/docs/html/M_MailKit_Net_Smtp_SmtpClient_ConnectAsync_1.htm). Check outbound firewall/provider restrictions on the configured port.

Direct ASP.NET hosting uses equivalent `Email__Enabled`, `Email__PublicUrl`, `Email__Host`, `Email__Port`, `Email__Security`, `Email__From`, `Email__Username`, and `Email__Password` environment variables. If enabled/host/from/public URL are absent or the sender, TLS mode, port or public origin are invalid, delivery is unavailable and the UI says so. Saving settings still works; request confirmation after configuration is complete. `Email__Security=None` and an HTTP public URL are supported only in the Development environment for a local SMTP capture server. Never enable Development on the VPS.

Apply configuration when recreating the app container with the normal deployment procedure. No new cron job or exposed SMTP listener is needed. Keep the existing `/data` volume: both SQLite outbox state and `/data/keys` Data Protection keys must persist together. Back up both before deploying. Losing keys prevents decrypting queued messages and invalidates unsubscribe links. Version 6 is a database migration; rolling back to an older app requires restoring its compatible pre-upgrade backup.

## Queue operations and verification

Pending payloads are encrypted using ASP.NET Data Protection. Tokens are stored as hashes in preferences; unsubscribe tokens are protected and scoped to an address revision. Terminal queue rows retain IDs, week, attempts and state, but message content is erased. Queue logs include IDs and retry state, never message content, addresses, SMTP credentials or raw exceptions.

Claims use a two-minute lease and each SMTP attempt has a 45-second deadline. Failed sends retry after 2, 4, 8, 16 and 32 minutes, then become `failed` on the sixth failure. Expired leases are reclaimed after restart. Old recaps, changed addresses, disabled preferences and expired verification messages are cancelled before sending. An SMTP message already in flight cannot be recalled. SMTP delivery is at-least-once: a crash after server acceptance but before recording success can duplicate delivery. Stable Message-ID helps diagnostics but does not guarantee provider deduplication.

Before enabling broadly, verify using an operator account:

1. Deploy the tested image with a database/key backup and configured provider. Check `/health` and worker logs.
2. Save an address with summaries enabled. Within a minute, receive both HTML and plain-text confirmation content; inspect spam folders and SPF/DKIM results.
3. Confirm using a signed-out browser. Reusing the link must fail; Profile should show the address confirmed.
4. Check the next eligible Monday recap: dates, weight change, grid, XP, rank and both email formats. Newly created accounts need one full eligible week first.
5. Open the unsubscribe link and press the button; Profile should show summaries disabled. Check address changes and resend after the one-minute cooldown.
6. In staging with an SMTP capture server, stop SMTP, observe retry state, restart the app, restore SMTP, and verify delivery resumes. Never test failures by sending to real unrelated addresses.

For troubleshooting, inspect only safe queue metadata with SQLite (do not select Email, Payload, TokenHash or credentials):

```sql
SELECT Kind, State, COUNT(*) AS Messages FROM EmailOutbox GROUP BY Kind, State;
SELECT Id, Kind, WeekStart, State, Attempts, NextAttempt, LeaseUntil, SentAt
FROM EmailOutbox ORDER BY NextAttempt DESC LIMIT 30;
```

Automated integration tests use a fake transport and a controllable UTC clock: preferences/CSRF, token expiry and reuse, address revision isolation, schedule deduplication, bonus correction, retry/restart, exclusive leases, terminal failures, cancellation, SMTP configuration, HTML escaping and challenge privacy. Frontend tests cover fragment removal, malformed links, explicit confirmation buttons and the CSRF submission path. No live SMTP credentials are needed. Production provider delivery is a separate operator check and has not been performed by this change.
