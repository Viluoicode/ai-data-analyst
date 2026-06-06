# Deployment

## Local stack — `docker-compose.yml`

Three services:
- `mssql` — SQL Server 2022, host port **11433**, healthcheck via `sqlcmd`.
- `mssql-init` — runs `db/01_schema.sql` → `02_seed.sql` → `03_readonly_role.sql` once the
  engine is healthy, then exits (`service_completed_successfully`).
- `api` — built from the `Dockerfile`, waits for the seed, serves on **:8080**, Offline
  provider by default.

```bash
docker compose up -d            # DB only (api builds on first --build)
docker compose up -d --build api
docker wait analyst-mssql-init  # 0 = seeded
```

## Container image — `Dockerfile`

Multi-stage: `mcr.microsoft.com/dotnet/sdk:10.0` restores+publishes `Analyst.Api` (which
content-includes `config/schema.fnb.json`), runtime is `mcr.microsoft.com/dotnet/aspnet:10.0`.
Listens on `ASPNETCORE_URLS=http://+:8080`; honors `$PORT` (Render/Railway/Fly). `.dockerignore`
keeps the context lean. `Program.cs` applies `$PORT` before host build.

## Production configuration (env vars)

```
ConnectionStrings__Analyst = Server=...;Database=AnalystDB;User ID=analyst_ro;Password=...;Encrypt=True;...
Analyst__Provider          = OpenAI            # or AzureOpenAI / Offline
Analyst__OpenAI__BaseUrl   = https://api.groq.com/openai/v1   # any OpenAI-compatible endpoint
Analyst__OpenAI__Model     = llama-3.3-70b-versatile
Analyst__OpenAI__ApiKey    = <provider key>    # never committed
```

The app reads `ConnectionStrings:Analyst` then env overrides. The DB user must be read-only.

## Azure (assets in `deploy/azure/`)

Target: Azure Container Apps (API, built from the `Dockerfile`) + Azure SQL Database (data).
- `01_schema.sql` / `02_seed.sql` — Azure-SQL variants (no `USE`/`CREATE DATABASE`; connect
  directly to `AnalystDB`). Same deterministic data.
- `03_role.sql` — a **contained** read-only user (`CREATE USER analyst_ro WITH PASSWORD`),
  not a server login (Azure SQL has no server-login-per-DB model here).
- `deploy.ps1` — scripts resource group, free-tier SQL, remote seed (throwaway `sqlcmd`
  container), and `az containerapp up --source .`. LLM defaults to **Groq (free)** via
  `$LLM_BASEURL`/`$LLM_MODEL`; the key is read from `$env:OPENAI_KEY`, never hardcoded.
- `deploy/azure/README.md` — prerequisites, steps, cost/safety notes, troubleshooting.

Free LLM options (OpenAI-compatible, no card): Groq, Google Gemini, GitHub Models. OpenAI
itself needs paid credit.

## Ad-hoc public demo — Cloudflare quick tunnel

When the container runs locally on `:8080`, expose it with no account/card:
```powershell
cloudflared tunnel --url http://localhost:8080
```
Prints a random `https://<words>.trycloudflare.com` URL. **Ephemeral** — alive only while the
process + Docker + machine run, and the URL changes each restart. Use it for live demos, not
as a permanent CV link. Safe to expose: the app is read-only (`analyst_ro`).

## CI — `.github/workflows/ci.yml`

On push/PR to `main`: job `build-and-test` (restore → build → 27 unit tests, DB-free) and job
`evaluate` (`docker compose up` → run `Analyst.Eval`, which fails on any safety regression).
Keep both green. README carries the CI badge.
