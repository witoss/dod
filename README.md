# DOD — discipline over dopamine

A personal journal for morning weight and evening calories burned, built with .NET 10, React, and SQLite. This README is both a guide to using the app and a tutorial explaining how it is built. As the app grows, new features should add their design decisions and explanations here.

## Contents

- [1. Install and run the app](#1-install-and-run-the-app)
- [2. Use your journal](#2-use-your-journal)
- [3. Develop and test locally](#3-develop-and-test-locally)
- [4. Understand the architecture and decisions](#4-understand-the-architecture-and-decisions)
- [5. Understand Docker and persistent data](#5-understand-docker-and-persistent-data)
- [6. How hot reload works inside Docker](#6-how-hot-reload-works-inside-docker)
- [7. Understand and use CICD](#7-understand-and-use-cicd)
- [8. Follow a change from idea to deployment](#8-follow-a-change-from-idea-to-deployment)
- [9. API reference](#9-api-reference)
- [10. Troubleshooting](#10-troubleshooting)

## 1. Install and run the app

### Prerequisites

For the container setup, install Git and [Docker Desktop](https://docs.docker.com/desktop/setup/install/mac-install/), which includes Docker Compose. On an Apple Silicon Mac, choose the Apple Silicon installer. Open Docker Desktop and wait for it to start.

You do not need to install .NET or Node.js on your Mac to run this container setup: the build uses them inside Docker.

Get the project if you do not already have it:

```sh
git clone https://github.com/witoss/dod.git
cd dod
```

Open `Dod.slnx` in Rider. Open **View → Tool Windows → Terminal** to run commands. Unless stated otherwise, commands in this guide run from the repository root—the folder containing `compose.yaml`.

Check Docker is available:

```sh
docker --version
docker compose version
```

### Configure your login

On first setup, copy the example configuration:

```sh
cp .env.example .env
```

If you already have `.env`, edit it instead of copying over it. Set your own unique password:

```dotenv
TRACKER_PASSWORD=replace-with-your-own-long-password
```

This password protects the website. It is not your Docker, Mac, or GitHub password. `.env` is excluded from Git and the Docker build context.

### Start the app

```sh
docker compose up --build -d
```

Open **http://localhost:8080**. Your browser asks for:

- **Username:** `tracker`
- **Password:** your `TRACKER_PASSWORD` value

The first build downloads dependencies and takes longer. Later builds reuse cached work. `--build` builds the image before starting; `-d` leaves the container running in the background.

### Everyday commands

| Task | Command |
| --- | --- |
| Start the existing app | `docker compose up -d` |
| Apply code changes | `docker compose up --build -d` |
| See container status | `docker compose ps` |
| Follow application logs | `docker compose logs -f app` |
| Stop without removing the container | `docker compose stop` |
| Stop and remove the container | `docker compose down` |

Press Ctrl+C to stop following logs; the app keeps running. Both stopping and removing the container preserve the named data volume. **`docker compose down -v` deletes that volume and your journal.**

## 2. Use your journal

### Record a day

1. Select the date above the entry forms. It defaults to your browser's local date.
2. In the morning, enter your weight in kilograms and select **Save weight**.
3. In the evening, enter calories burned in kilocalories (kcal) and select **Save calories**.
4. Use **Edit** in the journal to correct an earlier day or fill in a missed measurement.

The two saves are independent: saving calories preserves the weight for that date. Saving a measurement again replaces its previous value. A missing value is different from a recorded zero.

Use a consistent definition of calories burned, such as the total daily number reported by your watch. The app records the number you provide; it does not estimate it.

### Configure the reference and read the graph

Below the entry forms, find **Your calorie balance**. Enter a **Daily calorie reference** and select **Save reference**. The reference is saved in SQLite and survives restarts.

The calculation is:

```text
balance = reference − calories burned
```

| Reference | Burned | Balance | Meaning |
| --- | --- | --- | --- |
| 2200 kcal | 2500 kcal | −300 kcal | Burned more than the reference |
| 2200 kcal | 1800 kcal | +400 kcal | Burned less than the reference |
| 2200 kcal | 2200 kcal | 0 kcal | Matched the reference |

Choose a 7-, 14-, or 30-day period ending on the date selected above the entry forms. Hover, tap, or keyboard-focus a day for details. Expand the table below the graph to read the same daily balances as text.

Missing days are excluded from the average; a recorded zero is included. Changing the reference recalculates all historical comparisons without changing the recorded calories. The app uses one current reference, not a separate historical reference for each day.

This is a comparison with your burn reference. Calculating a food calorie deficit would also require calorie intake data.

## 3. Develop and test locally

### Run the development servers

For development outside Docker, install the **.NET 10 SDK** and **Node.js 24**. The SDK includes tools to build the backend; Node.js runs the frontend development tools.

In one terminal, start the API with file watching:

```sh
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5080 dotnet watch --project src/Dod.Api run
```

In a second terminal:

```sh
cd src/Dod.Web
npm ci
npm run dev
```

Open the Vite URL printed in the second terminal, normally `http://localhost:5173`. `npm ci` installs the dependency versions recorded in `package-lock.json`. Vite serves the React frontend and forwards `/api` requests to the backend at port 5080.

Save a React or CSS file to see Vite update the browser. Save C# code to let `dotnet watch` apply a supported change or restart the API.

Development disables the login unless `Tracker__Password` is set. Keep the development servers on loopback. The root `.env` is read by Docker Compose; `dotnet watch` does not automatically load it.

The local API uses `data/dod.db` relative to its working directory unless you set `Storage__Path`. This is separate from the database in Docker's named volume, so development and container runs can have different entries.

### Run checks

From the repository root:

```sh
dotnet test Dod.slnx --configuration Release
npm --prefix src/Dod.Web ci
npm --prefix src/Dod.Web test
npm --prefix src/Dod.Web run build
```

Backend integration tests call the HTTP API and use temporary real SQLite databases. They check independent updates, validation, password enforcement, persistence, reference settings, and upgrading the original database without losing measurements.

Frontend tests cover local calendar dates and balance calculations, including missing entries, zero values, and changing the reference. The frontend build checks TypeScript and generates production assets. Automated browser tests are not currently part of CI.

## 4. Understand the architecture and decisions

These steps explain the design as it stands and why each piece was introduced.

### Step 1: Start with the daily habit

The first requirement was small: one morning weight and one evening calorie entry per date. That led to independent measurement endpoints and one database row per calendar day.

A date is stored as a calendar date, such as `2026-09-29`, rather than an instant converted to UTC. This prevents a check-in near midnight from moving to another day because of a time-zone conversion.

### Step 2: Separate the screen from storage

React renders the screen and handles user interaction. TypeScript helps catch mistakes in the frontend's data handling. ASP.NET Core validates requests and persists measurements.

```mermaid
sequenceDiagram
    participant User
    participant React as React in the browser
    participant API as ASP.NET Core API
    participant DB as SQLite
    User->>React: Save calories for a date
    React->>API: PUT /api/entries/{date}/calories
    API->>API: Validate the value
    API->>DB: Insert or update only calories
    DB-->>API: Saved
    API-->>React: 204 No Content
    React->>API: Reload journal entries
    API-->>React: Entries as JSON
    React-->>User: Updated journal and graph
```

The server validates input even though the forms also have constraints: an API can be called without using the website. Invalid values return HTTP 400 instead of being stored.

### Step 3: Keep the backend small

The MVP uses one backend project with feature folders. There is no need yet for multiple services or separate domain, application, and infrastructure projects. Organizing by feature keeps related code easy to locate.

```text
Dod.slnx                          Rider/.NET solution
src/Dod.Api/Program.cs             Startup, authentication, and routing
src/Dod.Api/Entries/               Measurement endpoints and SQLite store
src/Dod.Api/Settings/              Calorie-reference endpoints
src/Dod.Web/src/main.tsx           Daily entry forms and journal
src/Dod.Web/src/calories/          Reference form, balance calculation, and graph
tests/Dod.Api.Tests/              API and persistence tests
Dockerfile                        Production image build
compose.yaml                      Local production-style container setup
.github/workflows/ci.yml           Automated checks and image publishing
docs/devops.md                    Hosting, backups, and rollback guide
```

The settings endpoints currently share the SQLite store with entries. If storage responsibilities grow, a dedicated persistence module can become useful. Add structure when it solves a concrete problem.

### Step 4: Persist data with SQLite

SQLite stores this personal journal in a file, so the MVP does not need a separate database server. SQL values are passed as parameters. Each measurement save performs an atomic insert-or-update for just its own field.

The container design uses one app instance and persistent disk. If the app later needs multiple instances or more complex multi-user hosting, reassess the storage design and consider a server database such as PostgreSQL.

### Step 5: Protect the personal journal

The MVP uses HTTP Basic authentication with one username, `tracker`. The password comes from configuration, and the app refuses to start outside Development without one.

Compose reads `TRACKER_PASSWORD` from `.env` and passes it to .NET as `Tracker__Password`. In .NET configuration, the double underscore represents a section separator: `Tracker:Password`.

This is a single-account design. Public hosting must use HTTPS because Basic authentication does not encrypt credentials itself. Multi-user accounts would require proper identity management and ownership checks for each user's data. Browsers retain Basic authentication credentials; a private browser session is useful when you need a clear session boundary.

### Step 6: Add the reference without rewriting measurements

The reference is stored separately from daily measurements. The frontend derives balances from the saved reference and the existing calorie entries. This makes changes to the reference immediately visible without rewriting history.

SQLite's `user_version` records the schema version. The version 2 startup migration adds the reference table in a transaction and adopts the original unversioned database without deleting entries. Tests exercise that upgrade. Future schema changes should introduce a new migration version.

### Step 7: Package one deployable app

During development, Vite and .NET run as separate processes. In the production image, Vite has already compiled React into static HTML, JavaScript, and CSS. ASP.NET serves those files and the API from the same address.

The browser still runs React. Node.js and Vite do not need to run in the production container.

## 5. Understand Docker and persistent data

### Image, container, and volume

| Term | Meaning in this project |
| --- | --- |
| Image | A packaged runtime and a built version of the app |
| Container | A running instance of that image |
| Named volume | Docker-managed storage holding the SQLite database |
| Bind mount | A host folder shared with a container; useful for source code during development |
| Dockerfile | Instructions for building an image |
| Compose file | Instructions for running containers, connecting ports, and attaching storage |

On your Mac, Docker Desktop runs the Linux containers through a Linux virtual machine. You normally interact with them through Docker commands rather than managing that machine yourself.

### Follow the production build

The [Dockerfile](Dockerfile) has three stages:

1. **Frontend build:** use Node.js to install dependencies and compile React.
2. **Backend build:** use the .NET SDK to publish the API, then copy the frontend assets into `wwwroot`.
3. **Runtime:** copy the finished app into an ASP.NET runtime image and run it as a non-root user.

The final image does not need the Node.js build tools or .NET SDK. Docker caches build steps and can reuse them when their inputs have not changed. Dependencies are copied and installed before the rest of the source so ordinary code edits can reuse dependency installation steps.

### Reach the app from your browser

Compose publishes this port mapping:

```yaml
ports:
  - "127.0.0.1:8080:8080"
```

The first `8080` is the port on your Mac; the second is inside the container. `127.0.0.1` limits access to your own machine. Your browser connects to the Mac's port, and Docker forwards the connection to ASP.NET.

### Preserve the database

Compose attaches the named volume `tracker-data` at `/data`. The image sets `Storage__Path=/data/dod.db`, so the API writes its database into that volume.

Replacing the app container does not replace the volume. Your records survive stopping Docker, restarting your Mac, and rebuilding the image. Volumes can still be deleted or lost, so they are not a substitute for backups. See the [backup and restore guide](docs/devops.md#4-backup-and-restore).

## 6. How hot reload works inside Docker

**This section explains a development setup we can add next. The current `compose.yaml` builds the production app; it does not enable hot reload.** The local development commands in section 3 already provide file watching outside Docker.

### Why the current image needs rebuilding

The production Dockerfile copies source code into the build. Editing a file in Rider changes your Mac's file, not the copy used to create an existing image. To include those edits, run:

```sh
docker compose up --build -d
```

### Share source files during development

A development container can use a bind mount. For example, this fragment shares only the React source folder with a container whose project is at `/app`:

```yaml
# Illustration only: this is not a complete Compose service.
volumes:
  - ./src/Dod.Web/src:/app/src
```

Rider edits the files on your Mac. The container sees those same files at `/app/src`. The container then runs Vite's development server instead of serving prebuilt assets.

```mermaid
flowchart TD
    A[Save a React or CSS file in Rider] --> B[Bind mount makes the edit visible in the container]
    B --> C[Vite detects the file change]
    C --> D[Vite sends an update to the browser]
    D --> E[The browser updates the affected UI]
```

**Vite performs hot reload; Docker supplies the environment.** Vite maintains a connection with the browser and sends updated modules. This is called *hot module replacement*. Many edits update the UI without a full page reload and can preserve component state; some changes still require a reload.

The backend follows the same pattern: share the C# source and run `dotnet watch`. It applies supported changes while running or restarts the API when required.

### What a complete development Compose setup would need

A future `compose.dev.yaml` would run a Node.js container for Vite and a .NET SDK container for the API. It would need source mounts, dependency storage, development commands, ports, and a separate development database volume.

Vite would listen on `0.0.0.0` inside its container so Docker can forward connections to it, while the published host port can remain bound to `127.0.0.1`. The API must likewise listen on an interface reachable from the other container.

There is one networking detail: inside the frontend container, `localhost` refers to that container. Its API proxy must target the backend's Compose service name, for example `http://api:5080`, rather than the current local-development proxy address `http://localhost:5080`.

Mounting an entire frontend project also hides files already present at the target path, potentially including `node_modules`. A complete setup must keep Linux container dependencies separate from the Mac's dependencies. On systems where file-change events do not propagate reliably, the watchers may need polling.

| Change | Typical development behavior |
| --- | --- |
| React component or CSS | Save; Vite updates the browser |
| C# code | Save; .NET applies the change or restarts |
| Dependencies | Reinstall in the development environment; rebuild if baked into its image |
| Dockerfile or runtime version | Rebuild the image |
| Check the deployable package | Build and test the production image |

The workflow is: edit with hot reload, run tests, then test the production image before shipping. A development container includes tools and shared source that the production package does not need.

## 7. Understand and use CI/CD

### What the terms mean

**Continuous integration (CI)** automatically checks changes when they reach GitHub. It helps catch problems before they enter the main branch.

**Continuous delivery** produces a deployable artifact after checks pass. Here, the artifact is a Docker image stored in GitHub Container Registry (GHCR).

**Continuous deployment** automatically updates a running environment. This repository does not yet do that. Publishing an image does not update your local container or a hosted website.

### Follow the current workflow

Read [.github/workflows/ci.yml](.github/workflows/ci.yml) alongside this explanation.

```mermaid
flowchart LR
    A[Pull request or push to main] --> B[Backend tests]
    B --> C[Frontend tests and production build]
    C --> D[Build Docker image]
    D --> E{Push to main?}
    E -->|Yes| F[Publish image to GHCR with commit SHA tag]
    E -->|No| G[Finish verification without publishing]
```

The `test` job installs .NET and Node.js on a temporary GitHub runner, then runs backend tests, frontend tests, and the frontend build. The `container` job waits for it to pass before building the image.

| Trigger | Checks and image build | Publish to GHCR | Deploy a website |
| --- | --- | --- | --- |
| Pull request | Yes | No | No |
| Push to `main` | Yes | Yes | No |
| Manual workflow run | Yes | No | No |

A published image is named `ghcr.io/<owner>/<repository>:<commit-sha>`. The SHA identifies the source revision used for the build, so you can select a specific release instead of relying on an ambiguous `latest` tag.

The publishing job uses GitHub's provided `GITHUB_TOKEN` with package-write permission. It does not need your application's `.env` password. Application secrets are supplied when a container runs, not built into its image.

### Use the pipeline

1. Create a branch for a change and run the local checks in section 3.
2. Commit and push the branch to GitHub, then open a pull request into `main`.
3. Open the pull request's checks or the repository's **Actions** tab. Read the failing step's logs if a check fails.
4. Merge after checks pass. The push to `main` runs the workflow and publishes an image if all steps succeed.
5. Inspect the workflow and the repository's associated package to find the resulting image tag.

Configure branch protection or a ruleset to require the checks before merging, where supported by your repository settings. The workflow file alone does not enforce that rule. Dependabot is configured to propose weekly dependency updates; review their checks before merging them.

### Add deployment later

Hosting has not been provisioned. The next stage is to select a host, attach persistent storage, configure HTTPS and runtime secrets, and deploy a tested image tag manually. Once that works, automate the same steps with health checks and a rollback plan.

See [docs/devops.md](docs/devops.md) for the operational details: hosting, backups, production approvals, and rollback. Database compatibility matters during rollback—an older image cannot automatically undo a schema change.

## 8. Follow a change from idea to deployment

Use this sequence when extending the app:

1. **Define the behavior.** Write an example, such as “reference 2200 and burned 2500 must display −300.” Decide how missing values and historical entries should behave.
2. **Choose where the change belongs.** UI and derived calculations live in React; input validation and storage live in the API. Persistent schema changes need a migration.
3. **Implement with development servers.** Use fast feedback from Vite and `dotnet watch`. Use development data for experiments.
4. **Check the behavior.** Add meaningful tests for new calculations or storage rules, run the relevant checks, and try the affected screen.
5. **Check the packaged app.** Build the production image. The existing Compose command updates your normal local tracker, so use it when you intend to update that app.
6. **Review through CI.** Open a pull request and inspect the automated results. After merging, identify the published image by its commit SHA.
7. **Deploy when hosting exists.** Deploy that image, verify health and key user actions, and keep the previous version available for rollback.
8. **Update this tutorial.** Explain what changed, why the design fits, how to use it, and any effect on existing data or deployment.

## 9. API reference

| Method | Route | Request body |
| --- | --- | --- |
| GET | `/api/entries/` | — |
| PUT | `/api/entries/2026-09-28/weight` | `{ "weightKg": 75.25 }` |
| PUT | `/api/entries/2026-09-28/calories` | `{ "caloriesBurned": 2400 }` |
| GET | `/api/settings/calorie-reference` | — |
| PUT | `/api/settings/calorie-reference` | `{ "calories": 2200 }` |
| GET | `/health` | — |

A measurement PUT creates or replaces only that measurement for the selected date. The reference GET returns `{ "calories": null }` until configured. Validation failures return HTTP 400; successful saves return 204. Routes are password-protected when authentication is enabled, except `/health`.

The health route is a liveness probe: it confirms that the process can respond. It is not a database readiness check.

## 10. Troubleshooting

| Symptom | What to check |
| --- | --- |
| `zsh: command not found: docker` | Install and launch Docker Desktop, then open a new terminal. If already installed, check that its CLI is on your PATH. |
| Docker cannot connect to its daemon | Start Docker Desktop and wait until it is running. |
| `TRACKER_PASSWORD is missing a value` | Create `.env` beside `compose.yaml` and set a nonempty `TRACKER_PASSWORD`. |
| Browser asks for credentials | Use username `tracker` and the password from `.env`. |
| Code changed but the container UI did not | Run `docker compose up --build -d`, then refresh the browser. Current Compose does not hot-reload. |
| The graph is absent | Save a calorie reference and record calories within the selected date range. |
| Local development shows different entries | The local API and Docker use separate database locations by default. |
| A port is already in use | Stop the conflicting process or choose another host port; use that port in the browser. |

For application errors, start with `docker compose logs -f app` and check `http://localhost:8080/health`.
