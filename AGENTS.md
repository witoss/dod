# Working on dodo

## Readability and maintainability

Write code that a human can understand and maintain. Prefer clear names, explicit control flow, and cohesive functions and classes. Avoid dense one-liners, clever shortcuts, and comments that merely repeat the code. Explain non-obvious business rules and transaction boundaries.

Assess the code touched by a feature or fix for duplication, mixed responsibilities, and growing complexity. Make small refactors when they make the change easier to understand or test. Preserve behavior during refactoring and verify it with relevant tests. Do not turn a local change into a repository-wide rewrite or introduce abstractions without a concrete benefit. For a larger refactor, explain its purpose and scope before starting.

Apply Domain Driven Design pragmatically: keep business rules independent of HTTP, SQL, and email providers where useful. Use the project's domain terms consistently. Prefer the existing `Motivation/Domain` and `Motivation/Persistence` boundaries over building a new framework. Keep endpoints focused on authorization, input validation, and calling application operations. Add middleware only for concerns that genuinely span requests.

## Business rules to preserve

- Users may correct available manual goals for today or past UTC dates, including dates without weight or calorie entries. Future dates cannot earn XP.
- First reports snapshot the reward and goal wording. Corrections use that snapshot; repeated or concurrent saves must not duplicate rewards.
- Report changes, weekly bonus reconciliation, and the authoritative XP adjustment happen in one database transaction. A correction may add or remove the affected closed week's 50 XP bonus; other weeks and users must remain unaffected.
- The weekly roster is fixed at Monday. New and retired goals affect the next Monday's roster. New goals can earn daily XP immediately. Weekly bonuses require all roster goals on all seven days of a closed eligible week; onboarding and empty-roster weeks do not earn bonuses.
- Password recovery requires an already saved and verified email address, independently of weekly email opt-in. Recovery tokens expire, work once, and are invalidated when the address changes. Successful reset revokes existing sessions for that account.
- Database migrations must preserve existing accounts, journals, XP, and email preferences. Document schema and rollback implications when changing storage.

These are the current product rules. Change them only when the user's request calls for a behavior change, and update the corresponding tests and documentation together.

## Tests and verification

Use tests that check observable behavior and business invariants. For XP, bonuses, account recovery, and persistence changes, cover relevant boundaries, retries/repeated requests, concurrency, isolation between users, and restart behavior. For frontend interactions, verify navigation, successful changes, and failed saves without optimistic corruption of saved state. Use controllable clocks and fake email transports; do not send real emails in automated tests.

Run the checks relevant to the change:

```sh
dotnet test Dod.slnx --configuration Release -m:1 -p:UseSharedCompilation=false
cd src/Dod.Web
npm test
npm run build
```

The .NET SDK version and roll-forward policy are in `global.json`. If `dotnet` is absent from PATH on the owner's Mac, Rider's SDK is available at `/Applications/Rider.app/Contents/lib/ReSharperHost/macos-arm64/dotnet/dotnet`. Test runners may need local socket access. Do not change project settings to bypass a sandbox restriction.

Documentation-only changes do not require the application test suite. Once appropriate checks pass, do not repeat them without a new change or unresolved concern. Report what ran and any checks that could not run.

## Logging and security

Use structured logs with operation names, internal IDs, request IDs, and relevant state or XP adjustments. Log committed changes, not success before a transaction commits. Prefer Debug for routine no-op operations. Keep existing request tracing, authorization, CSRF checks, and no-store API responses intact.

Never log passwords, reset/verification tokens, email contents, cookies, authorization headers, or raw provider responses. Keep email delivery behind the existing transport and persistent queue. Do not add unbounded in-memory queues or bypass the retry and cancellation rules.

Do not read real `.env` files, `release.env`, private keys, or other credentials. Public example configuration may be read. Use dummy values for configuration checks and never print rendered configuration containing secrets.

## Collaboration and documentation

Inspect the current Git diff before edits and preserve unfinished changes. Keep progress updates concise and frequent (about once a minute during ongoing work); focus on findings and remaining work. Continue authorized work without repeatedly asking for confirmation.

Read only the documentation relevant to the task: `knowledge-base.md` for SQLite and storage decisions, `docs/motivation.md` for goals and XP, `docs/weekly-emails.md` for scheduling and delivery, `docs/password-reset.md` for recovery, and `docs/vps.md` for deployment. Update the relevant guide when behavior or setup changes. Avoid putting implementation details into product UI text unless they help users make a decision.

Finish with the resulting behavior, verification, and any material limitations. Clearly distinguish local changes from deployed behavior. Keep this file concise and update rules when the product changes instead of relying on old chat context.
