# Future requirements: analytics and observability

Status: **planned, not implemented**. This document records ideas and directions, not an approved vendor or a deployment configuration. Existing application logs are separate from the proposed centralized monitoring system.

## 1. What we want to learn

There are two related areas:

- **Product analytics:** what people do in the app, which features they use, how often they return, and which steps they abandon.
- **Operational observability:** whether the app works, how quickly it responds, and why something failed.

| Signal | Example question | Proposed graph or view |
| --- | --- | --- |
| Product events | How often do users open a challenge chart? | Events and unique users per day |
| Funnels | How many users create a challenge after opening the form? | Conversion between steps |
| Retention | Do new users return the following week? | Returning-user cohorts |
| Metrics | Are requests slower or errors more frequent? | Request rate, error rate, p95 latency |
| Logs | Why did this request fail? | Searchable structured error records |
| Traces | Which part of a request took the time? | Timeline of request and database operations |

A metric is an aggregate measurement. A log describes an individual event. A trace follows one operation through its component steps. Correlating them makes it possible to move from a spike on a graph to the requests that explain it.

## 2. Product analytics requirements

Start with explicitly named events rather than recording every click. Proposed events include:

- `page_viewed` and `challenge_chart_opened` for navigation and feature discovery.
- `weight_saved`, `calories_saved`, and `activity_reported` for successful daily use.
- `friend_request_sent` and `friend_request_accepted` for social adoption.
- `challenge_created`, `challenge_invitation_sent`, and `challenge_joined` for challenge participation.

Record UI interactions in React and successful business operations on the backend. Distinguish an attempted action from a successful save. Define which source owns each event so the same operation is not counted twice; account for retries and repeated saves when defining counts.

Use an internal pseudonymous account identifier for unique-user and retention calculations. Pseudonymous does not mean anonymous. Define an active user as someone performing a meaningful action, with daily and weekly counts. Separate total events from distinct users and distinct reporting days.

Initial dashboards should show feature popularity, daily/weekly active users, daily reporting frequency, weekly retention, and a challenge creation/joining funnel. Define each funnel carefully: invitations and acceptances often involve different people, so they cannot automatically be treated as one user's conversion journey.

## 3. Operational monitoring requirements

- Public HTTPS availability and certificate expiry, checked from outside the VPS.
- A lightweight session-token check in addition to `/health`. A healthy process does not prove that login works: this app previously returned a healthy status while the CSRF endpoint failed behind the proxy.
- HTTP request rate, server error rate, and latency percentiles by route template and status.
- VPS CPU, memory, disk usage, container restarts, and database storage growth.
- Backup age and outcome, with restore exercises tracked separately from backup success.
- Structured backend logs and frontend error reporting, linked to a release version or Git commit SHA.
- Request traces with trace/span IDs available in associated logs; database instrumentation should avoid exposing query values.

Dashboards should allow filtering by service, environment, and deployed release. A deployment marker helps compare behavior before and after a release. Use route templates such as `/api/social/challenges/{id}`, not arbitrary URLs or user IDs, as metric labels to avoid excessive numbers of time series.

Begin with a small set of actionable alerts: public endpoint unavailable, sustained elevated server errors, low disk space, and overdue backups. Choose thresholds and notification destinations during implementation. Synthetic checks must not modify the owner's real journal.

## 4. Candidate tools and tradeoffs

### Suggested first direction: hosted dashboards, standard instrumentation

**PostHog** is a candidate for product events, funnels, retention, and usage dashboards. Its [funnels](https://posthog.com/docs/product-analytics/funnels) and [retention](https://posthog.com/docs/product-analytics/retention) documentation illustrate the questions it can answer.

**OpenTelemetry** is the proposed instrumentation standard for backend logs, metrics, and traces. It is not itself the dashboard or long-term storage system. See [Microsoft's .NET observability guide](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/observability-with-otel).

**Grafana Cloud** is a candidate destination for operational telemetry and dashboards. Hosted storage avoids maintaining a monitoring stack on the same small VPS as the app. Review supported collection methods, data region, retention, current pricing, and usage limits before choosing it. See [Grafana application observability](https://grafana.com/docs/grafana-cloud/observe-and-act/monitor-applications/).

This combination separates product questions from operational diagnosis while keeping backend instrumentation portable. Frontend error collection and correlation need an explicit implementation choice; adding backend OpenTelemetry alone does not capture browser errors.

### Alternative learning path: run the monitoring stack yourself

Use **Grafana** for dashboards, **Prometheus** for metrics, **Loki** for logs, and **Tempo** for traces. These are separate components with their own storage and maintenance requirements; Grafana alone does not collect or store all telemetry. See [Grafana data sources](https://grafana.com/docs/grafana/latest/datasources/).

Start this experiment locally with Docker Compose. It is useful for learning DevOps, but adds resource usage, upgrades, retention configuration, backups, and access control. Decide on production capacity after measuring it. Monitoring hosted only on the application's VPS can disappear during the same outage, so retain an external availability check.

## 5. Data boundaries and reliability

- Do not send actual weight, calorie values, activity answers, nicknames, avatars, passwords, cookies, tokens, request bodies, or database parameter values to analytics or diagnostic services.
- Allowlist event properties and sanitize URLs, exceptions, and logs. Use route names rather than URLs containing identifiers or query values.
- Keep session replay and broad automatic click/form capture disabled initially. Consider them separately only if needed, with masking and a privacy/consent review before enabling collection.
- Decide what notice, consent, opt-out, deletion, retention, and data-region controls are appropriate before release. Restrict dashboards to authorized administrators.
- Separate local/test traffic from production and provide a local opt-out or disabled default.
- Define retention, sampling, volume limits, and budget alerts. Avoid account IDs as metric labels.
- Telemetry delivery must not block journal saves or login. Use bounded buffering and graceful failure when a provider is unavailable.
- Keep provider credentials in deployment secrets, never in Git or browser bundles. Only publish client keys explicitly designed by the chosen provider for browser use.

## 6. Proposed implementation sequence

1. Agree on the first questions, event definitions, privacy boundaries, providers, and budget. No feature should be tracked without a purpose.
2. Establish public uptime checks and structured error logs with release metadata; confirm that checks catch a broken session flow even when `/health` is healthy.
3. Add a small set of explicit product events and build usage, retention, and conversion dashboards. Verify that failed actions do not count as successful saves and retries do not inflate results.
4. Add OpenTelemetry request metrics and traces, then build performance and VPS dashboards. Confirm that an error can be followed from its graph to a trace and corresponding logs.
5. Add actionable alerts and document how to investigate each one. Test delivery and behavior when the telemetry provider is unavailable.
6. Expand only when a real question needs more data. Experiment with self-hosted components locally as a separate DevOps exercise.

Before implementation, choose hosted versus self-hosted storage, budget, retention, administrators, alert recipients, and how telemetry data is handled when an account is deleted. No services, dependencies, events, or deployment changes are introduced by this plan.
