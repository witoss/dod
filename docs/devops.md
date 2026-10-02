# DevOps learning path

We have chosen a Hetzner VPS. Follow the concrete [VPS deployment tutorial](vps.md) for the prepared production files, server setup, first release, and manual GitHub deployment workflow. Infrastructure has not yet been provisioned.

## 1. Understand the local deployment

`docker compose up --build` builds React assets, publishes .NET, then packages both in a runtime image. The final container runs as a non-root user. Only port 8080 is exposed, and Compose binds it to localhost. The database lives on a named volume, outside the disposable container.

Learn to inspect `docker compose ps`, read `docker compose logs`, restart the service, and verify that entries survive a rebuild. `/health` confirms the process can respond. Structured ASP.NET logs go to standard output.

## 2. Continuous integration and delivery

Push the repository to GitHub. Pull requests run backend integration tests, frontend tests, TypeScript checking, the production frontend build, and a container build. Protect `main` with required checks and PR review where your GitHub plan permits it.

After tests pass on `main`, Actions publishes `ghcr.io/<owner>/dod:<commit-sha>`. The SHA identifies the exact revision for deployment and rollback. Package publishing uses the workflow's scoped `GITHUB_TOKEN`; no registry password belongs in the repository. Dependabot opens dependency updates weekly. Review and merge those only after checks pass.

The `ci.yml` workflow builds and publishes images. The separate `deploy.yml` workflow can manually deploy a successful main-branch image once the VPS and SSH secrets are configured. Continuous delivery makes an artifact available; continuous deployment rolls it out automatically. Learn the first before enabling the second.

## 3. First hosted release

The simplest fit for the current SQLite design is one Linux server with Docker, persistent disk, and a reverse proxy that terminates HTTPS. Alternatively choose a container host with a persistent volume. Do not put the database on an ephemeral filesystem or run multiple replicas. Hosting costs and providers should be selected before provisioning anything.

A first deployment checklist:

1. Configure a domain and HTTPS reverse proxy to the app's localhost port 8080. Expose only HTTPS publicly, plus restricted administrative access. Account credentials must never travel over public plain HTTP; production session cookies require HTTPS.
2. Store a strong `Tracker__Password` in the host's secret configuration. Keep `.env` out of Git. Add proxy-level login rate limiting before exposing the app publicly.
3. Pull the GHCR image by commit SHA; a private package needs a read-only registry credential on the host. Replace Compose's `build: .` with `image: ghcr.io/<owner>/dod:<commit-sha>` for artifact-based deployments.
4. Attach a persistent volume at `/data`, writable by the container's `app` user. The supplied named volume initializes permissions from the image; a host bind mount requires explicit ownership setup.
5. Start the container; check `/health`, authenticate, save an entry, restart, and verify it remains.
6. Set up off-host backups and test a restore before relying on the journal.

Keep one running instance. Individual cookie-based accounts and per-user journal ownership now support friends and challenges. Follow the [account upgrade guide](social.md) to claim existing data, and review its recovery and rate-limiting limitations before wider use.

## 4. Backup and restore

SQLite uses WAL: copying only the live `.db` file is not a reliable backup. Either use SQLite's online backup command through suitable host tooling, or stop the application and archive the entire named volume, including any WAL files. Encrypt backups and store them off the server.

For a cold backup: stop the app with `docker compose stop app`, locate the volume using `docker volume ls` and `docker volume inspect`, archive its contents using your host/volume backup tooling, then `docker compose start app`. For restore, stop the app, restore into an empty volume, preserve the app user's write permissions, start, and inspect the journal. Rehearse this on a separate deployment; a backup is useful only when it restores successfully.

## 5. Deployment automation

After the manual deployment works, configure the prepared workflow with a GitHub `production` environment with a required reviewer. A deployment job should depend on CI, deploy the tested SHA, wait for health, and preserve the previous SHA for rollback. Prefer short-lived cloud identity credentials where your selected provider supports them. Scope deployment credentials to this application.

Rollback means redeploying the previous image SHA. Database changes also need a compatibility plan: startup now uses SQLite `user_version` migrations. Version 2 adds the calorie reference; version 3 adds optional daily calories eaten. Version 4 adds individual accounts and social features, reserving existing measurements for the original owner until claimed. These migrations preserve journal measurements. Future schema changes need a new migration version and a backup before deployment. A binary rollback cannot undo incompatible schema changes.

## 6. Extend when needed

Next useful exercises: browser end-to-end tests, database readiness checks, uptime alerts, automated encrypted backups, infrastructure as code, and then deployment automation. Add PostgreSQL if you need multiple instances or multi-user hosting. No orchestration cluster is needed for this MVP.
