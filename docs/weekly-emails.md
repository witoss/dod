# Weekly summaries and email delivery

In Profile, save your email address and choose **Send me weekly summaries**. A confirmation email is queued when you save a new address and delivery is configured. Open the link and press **Confirm email**; no login is required. Confirmation links expire in 24 hours and work once. **Resend confirmation** is limited to once per minute. Changing the address resets verification and invalidates old links. An unverified address never receives a recap.

Turn summaries off in Profile or open **Stop weekly emails** in a recap and press **Unsubscribe**. Link visits alone do not change preferences. The address is private and is never included in social or admin account responses. Email confirmation is separate from nickname/password login; it does not provide password recovery.

In Profile, **Send test summary** queues a recap of the current Monday–Sunday, using available data even while the week is unfinished to your own confirmed address. Weekly opt-in is not required. The subject starts with `[Test]`; this does not consume the scheduled weekly recap slot. Allow about a minute for the worker to submit it. Only one test can be pending/in flight, and successful sends have a one-minute cooldown. Test sends use the same retry queue; changing the saved address before delivery cancels the old test.

## Week boundaries and XP

Weeks run Monday through Sunday in UTC. Existing accounts become eligible on the Monday after the database upgrade; newly registered accounts become eligible on the Monday after registration. There are no bonuses for earlier weeks or partial onboarding weeks.

The weekly activity roster is fixed at Monday. Admin additions, name edits, activation and retirement affect the next Monday's roster. Daily point and description changes still follow the existing daily activity rules. A retired goal remains reportable for weeks where it belonged to the roster; saved reports keep their original XP. Extra activities can earn daily XP without entering that week's roster.

Every goal in the roster must be completed on all seven days for a 50 XP bonus. An empty roster earns no bonus. Missing and negative reports do not qualify. Weight goals complete when weight is recorded. Bonuses are computed for all accounts, even without email opt-in or delivery configuration. Correcting a closed week's report can add or remove its bonus; experience totals and admin totals include bonuses. The worker checks closed weeks after startup and each minute.

Recaps are queued from Monday **08:00 UTC**, normally within one minute. Only the latest closed eligible week is queued, at most once per account/week. Restarting catches up that recap during the following week without sending older backlogs. Enabling or confirming midweek can therefore queue the previous week's recap. Failed or cancelled recaps are not recreated for the same week.

Weight change compares the first and last measurements inside the week; two measurements are required. Challenge scores compare accepted participants' first and last measurements from challenge start through Sunday, respecting the challenge's exclusive end date. Invitations show their status without revealing standings. Future measurements do not affect rankings. Recaps are snapshots at preparation time; corrections appear in the app and do not send replacement emails.

## Email API

All mutations use the existing antiforgery token. Preference routes require a signed-in account; link actions use the protected token and work signed out. Responses and errors are never cached.

| Route | Purpose |
| --- | --- |
| `GET /api/email/preferences` | Own email, enabled/verified flags and sending availability |
| `PUT /api/email/preferences` | Save `{ "email": "you@example.com", "enabled": true }` |
| `POST /api/email/resend` | Queue confirmation for the saved address |
| `POST /api/email/test-summary` | Queue current week's test recap to own confirmed address; session and CSRF required |
| `POST /api/email/verify` | Confirm `{ "token": "…" }` |
| `POST /api/email/unsubscribe` | Disable summaries using `{ "token": "…" }` |
| `GET /api/motivation/weekly/{date}` | Own recap, normalizing date to Monday |

## Production email setup

Use a transactional email provider and a sender address/domain that you control. Configure SPF, DKIM and DMARC using that provider's DNS instructions; check alignment with the From domain. See [Google's sender guidelines](https://support.google.com/mail/answer/81126?hl=en). The application sends HTML and plain-text content with an unsubscribe link in the body. It does not implement asynchronous provider bounce processing or RFC 8058 one-click unsubscribe headers. SMTP setup is below; the Cloudflare HTTPS setup follows it.

The production Compose file forwards these variables to ASP.NET configuration. Add values privately to the VPS configuration file used by the existing deployment scripts. Keep that file mode `600`, outside Git. Do not paste credentials into chat, logs, commands, or this document. Do not `source` the file or print rendered Compose configuration containing passwords.

```dotenv
EMAIL_ENABLED=true
EMAIL_PROVIDER=Smtp
EMAIL_PUBLIC_URL=https://your-journal.example
EMAIL_SMTP_HOST=smtp.your-provider.example
EMAIL_SMTP_PORT=587
EMAIL_SMTP_SECURITY=StartTls
EMAIL_FROM='dodo <summaries@your-domain.example>'
EMAIL_SMTP_USERNAME=your-provider-username
EMAIL_SMTP_PASSWORD='replace-privately-with-your-provider-secret'
```

`EMAIL_PUBLIC_URL` is the public HTTPS origin, without a trailing slash, query, or fragment. It must match the journal people open. Use the provider's submission port: normally `587` with `StartTls`, or `465` with `SslOnConnect`. Production never allows plaintext SMTP or a certificate validation bypass. [MailKit documents its TLS connection options](https://mimekit.net/docs/html/M_MailKit_Net_Smtp_SmtpClient_ConnectAsync_1.htm). Check outbound firewall/provider restrictions on the configured port.

Direct ASP.NET SMTP hosting uses equivalent `Email__Provider=Smtp`, `Email__Enabled`, `Email__PublicUrl`, `Email__Host`, `Email__Port`, `Email__Security`, `Email__From`, `Email__Username`, and `Email__Password` environment variables. If enabled/host/from/public URL are absent or the sender, TLS mode, port or public origin are invalid, SMTP delivery is unavailable and the UI says so. Saving settings still works; request confirmation after configuration is complete. `Email__Security=None` and an HTTP public URL are supported only in the Development environment for a local SMTP capture server. Never enable Development on the VPS.

Apply configuration when recreating the app container with the normal deployment procedure. The updated GitHub deployment workflow copies the production Compose file from the tested release before deployment. For manual releases, copy `deploy/vps/compose.yaml` to `/opt/dod/compose.yaml` yourself; older workflow versions only update the image. If Profile says delivery is unavailable, check that the server Compose file contains `Email__Enabled` and recreate the app after updating it. No new cron job or exposed SMTP listener is needed. Keep the existing `/data` volume: both SQLite outbox state and `/data/keys` Data Protection keys must persist together. Back up both before deploying. Losing keys prevents decrypting queued messages and invalidates unsubscribe links. Version 6 is a database migration; rolling back to an older app requires restoring its compatible pre-upgrade backup.

### Cloudflare HTTPS API (recommended on this Hetzner VPS)

The application supports Cloudflare's [REST sending API](https://developers.cloudflare.com/email-service/api/send-emails/rest-api/) over HTTPS port 443. No Worker deployment or SMTP port unblock is needed. The existing weekly scheduler, confirmation links and persisted retry queue work with either provider.

Privately edit `/opt/dod/.env` with `nano /opt/dod/.env` and set:

```dotenv
EMAIL_ENABLED=true
EMAIL_PROVIDER=Cloudflare
EMAIL_PUBLIC_URL=https://dodop.duckdns.org
EMAIL_FROM='dodo <summaries@dodojournal.com>'
EMAIL_CLOUDFLARE_ACCOUNT_ID=replace-with-your-32-character-account-id
EMAIL_CLOUDFLARE_API_TOKEN='replace-privately-with-your-existing-token-value'
```

Use the **Account ID** for the Cloudflare account owning `dodojournal.com`, not the Zone ID or API token ID. Copy it from the domain Overview page's API section in the Cloudflare dashboard. Use the actual token value with **Email Sending: Edit** permission; the existing SMTP token can be reused if it has that permission and belongs to the sender's account. The sender domain still needs to be ready under **Email Sending**, and account sending entitlement/recipient restrictions still apply. DNS can remain at Cloudflare and the website can remain at `dodop.duckdns.org`.

SMTP host, port, username and password are ignored when `EMAIL_PROVIDER=Cloudflare`. No fallback to SMTP occurs after API failure. Direct ASP.NET configuration uses `Email__Provider`, `Email__Cloudflare__AccountId` and `Email__Cloudflare__ApiToken` alongside enabled/from/public URL. Missing or invalid Cloudflare settings make delivery unavailable; an unknown provider name fails startup to expose the typo.

Commit and push the application/configuration changes, wait for CI and Deploy to succeed, then recreate the app if you edited the VPS configuration after deployment:

```bash
cd /opt/dod
docker compose --env-file .env --env-file release.env up -d --force-recreate app
```

In Profile, press **Resend confirmation** after the one-minute cooldown. Previously failed/expired confirmations are not revived automatically. Check safe logs:

```bash
docker logs --since 10m --tail 200 dod-production-app-1 2>&1 | grep -Ei 'EmailQueue|EmailWorker|accepted by email provider|cloudflare-'
```

`cloudflare-http-401` means token authentication failed; 403 indicates permission, entitlement or sending restrictions; 429 means throttling; 5xx indicates a provider failure. `cloudflare-api-rejected`, `cloudflare-invalid-response`, `cloudflare-permanent-bounce` and `cloudflare-recipient-not-accepted` are also failures. Consult Cloudflare's dashboard for details; response bodies and addresses are never logged by the app. Only a successful API response listing the recipient as delivered or queued counts as acceptance.

## Queue operations and verification

Pending payloads are encrypted using ASP.NET Data Protection. Tokens are stored as hashes in preferences; unsubscribe tokens are protected and scoped to an address revision. Terminal queue rows retain IDs, week, attempts and state, but message content is erased. Queue logs include IDs, retry state, safe error categories and the failing transport step, never message content, addresses, provider credentials or raw exception messages. A successful submission logs `accepted by email provider`; this confirms provider acceptance, not delivery to the inbox.

Claims use a two-minute lease and each send attempt has a 45-second deadline. Failed sends retry after 2, 4, 8, 16 and 32 minutes, then become `failed` on the sixth failure. Expired leases are reclaimed after restart. Old recaps, changed addresses, disabled preferences and expired verification messages are cancelled before sending. A message already in flight cannot be recalled. Both transports provide at-least-once submission: a crash after server acceptance but before recording success can duplicate delivery. SMTP uses a stable Message-ID for diagnostics; neither transport guarantees provider deduplication. The Cloudflare transport does not automatically retry inside the HTTP client; the persisted queue controls retries.

Before enabling broadly, verify using an operator account:

1. Deploy the tested image with a database/key backup and configured provider. Check `/health` and worker logs.
2. Save an address with summaries enabled. Within a minute, receive both HTML and plain-text confirmation content; inspect spam folders and SPF/DKIM results.
3. Confirm using a signed-out browser. Reusing the link must fail; Profile should show the address confirmed.
4. Check the next eligible Monday recap: dates, weight change, grid, XP, rank and both email formats. Newly created accounts need one full eligible week first.
5. Open the unsubscribe link and press the button; Profile should show summaries disabled. Check address changes and resend after the one-minute cooldown.
6. In staging with an SMTP capture server, stop SMTP, observe retry state, restart the app, restore SMTP, and verify delivery resumes. Never test failures by sending to real unrelated addresses.

If confirmation does not arrive, first check that the deployed Compose file has the email environment mappings and that Profile offers **Resend confirmation**. Press it once, allow a minute, then inspect the worker logs:

```bash
grep -n 'Email__Enabled' /opt/dod/compose.yaml
docker logs --since 15m --tail 500 dod-production-app-1 2>&1 | grep -Ei 'EmailQueue|EmailWorker|attempt|accepted by email provider|worker failed|payload'
```

| Log reason | Check |
| --- | --- |
| `smtp-authentication` or `smtp-535-*` | SMTP username/password, token permissions and expiry |
| `smtp-550-*` | Provider sender-domain verification and recipient rejection |
| `tls-handshake` or `tls-authentication` | Port/TLS pairing, certificates and server clock |
| `connection-*` | SMTP hostname, DNS, outbound firewall and provider connectivity |
| `cloudflare-http-*` or `cloudflare-*` | Cloudflare API token, account entitlement, sender readiness and provider delivery logs; see the HTTPS setup above |
| `timeout-or-shutdown` | Slow connection, blocked port or container restart |
| `database-*` | Database availability and writable persistent storage |
| `unexpected-<exception type>` | Use the exception type and `stage` (prepare, connect, authenticate, send, disconnect) to identify the failing operation; raw messages remain private |
| `accepted by email provider` | Provider delivery logs, suppression list and recipient spam folder |

If no attempts appear and delivery is unavailable, update the server Compose file and recreate the app. If submissions fail, identify the safe reason before changing credentials. For Cloudflare through SMTP specifically, use `smtp.mx.cloudflare.net`, port `465`, `SslOnConnect`, literal username `api_token`, and the token value as password. The token needs **Email Sending: Edit**, and the sender domain must be onboarded under **Email Sending**. See [Cloudflare's SMTP troubleshooting](https://developers.cloudflare.com/email-service/api/send-emails/smtp/).

On Hetzner Cloud, outbound ports **25 and 465 are blocked by default**, while 587 is allowed. Cloudflare SMTP requires 465 and does not support 587, so a connect-stage timeout can indicate this account restriction. Test from the VPS without credentials:

```bash
nc -vz -w 10 smtp.mx.cloudflare.net 465
```

A timeout supports a connectivity problem; it does not identify the blocking firewall by itself. Request an unblock through Hetzner after one month as a customer and payment of the first invoice (approval is case by case), or use another SMTP provider on 587 with `StartTls`. Use `EMAIL_PROVIDER=Cloudflare` for the HTTPS API transport described above to avoid the SMTP port restriction. Opening an inbound firewall port does not remove Hetzner's outbound account restriction. See [Hetzner's mail-port policy](https://docs.hetzner.com/cloud/servers/faq/#why-can-i-not-send-any-mails-from-my-server) and [Cloudflare's supported SMTP connection](https://developers.cloudflare.com/email-service/api/send-emails/smtp/).

For troubleshooting, inspect only safe queue metadata with SQLite (do not select Email, Payload, TokenHash or credentials):

```sql
SELECT Kind, State, COUNT(*) AS Messages FROM EmailOutbox GROUP BY Kind, State;
SELECT Id, Kind, WeekStart, State, Attempts, NextAttempt, LeaseUntil, SentAt
FROM EmailOutbox ORDER BY NextAttempt DESC LIMIT 30;
```

Automated integration tests use a fake transport and a controllable UTC clock: preferences/CSRF, token expiry and reuse, address revision isolation, schedule deduplication, bonus correction, retry/restart, exclusive leases, terminal failures, cancellation, SMTP configuration, HTML escaping and challenge privacy. Frontend tests cover fragment removal, malformed links, explicit confirmation buttons and the CSRF submission path. No live provider credentials are needed. Production provider delivery is a separate operator check and has not been performed by this change.
