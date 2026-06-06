# CLAUDE.md

Routing index for the **AI Data Analyst** codebase. This file is high-level only.
Before modifying a subsystem, read the matching file in `.claude/docs/`.

## 1. Project Overview

A .NET backend service that converts natural-language business questions (Vietnamese
or English) into **safe, read-only SQL**, runs them against a SQL Server analytics
("gold") schema, and returns result rows plus an optional natural-language summary.
The engineering value is the safety/correctness layer around the LLM — not the LLM
call: every generated query is parsed to a T-SQL AST, validated against a schema
whitelist, and executed through a least-privilege read-only database account. Accuracy
is measured by an evaluation harness.

## 2. Tech Stack

- Runtime: **.NET 10** (LTS), C#, ASP.NET Core Minimal API. Solution: `AiDataAnalyst.slnx`.
- LLM abstraction: `Microsoft.Extensions.AI` 10.6.0 (`IChatClient`).
- Providers: Azure OpenAI (`Azure.AI.OpenAI` 2.1.0), OpenAI/Ollama (OpenAI SDK via
  `Microsoft.Extensions.AI.OpenAI` 10.6.0), plus an offline canned client (no keys).
- SQL validation: `Microsoft.SqlServer.TransactSql.ScriptDom` 180.18.1 (real T-SQL parser).
- Data access: `Microsoft.Data.SqlClient` 7.0.1; database is **SQL Server 2022** (Docker).
- MCP server: `ModelContextProtocol` 1.4.0 (stdio).
- API docs: `Microsoft.AspNetCore.OpenApi` 10.0.8. Tests: xUnit 2.9.3.

## 3. Dev Commands

```bash
# 1. Database: SQL Server 2022 + schema + deterministic seed + read-only role
docker compose up -d
docker compose logs mssql-init          # expect "DB init complete."

# 2. Build
dotnet restore AiDataAnalyst.slnx
dotnet build   AiDataAnalyst.slnx -c Release

# 3. Run the API (offline provider by default; demo UI at "/", default port 5184)
dotnet run --project src/Analyst.Api

# 4. Tests (27, DB-free) and the evaluation scoreboard (needs the DB up)
dotnet test tests/Analyst.Tests
dotnet run  --project tests/Analyst.Eval -c Release

# 5. Full stack in containers (API on :8080)
docker compose up -d --build api
```

Config precedence: `appsettings.json` < user-secrets (Development only) < environment
variables (`ConnectionStrings__Analyst`, `Analyst__Provider`, `Analyst__OpenAI__*`).

## 4. Core Logic Summary

`AnalystService.AskAsync`: question → `ITextToSqlGenerator` (LLM, temperature 0, JSON
output) → `SqlValidator` (AST checks: single `SELECT`, table/column whitelist, banned
constructs, injected `TOP` row cap) → on validation failure, **one** repair retry →
`SqlExecutor` (read-only principal, command timeout, hard row cap) → optional
`ISummarizer`. The LLM is untrusted; validation is mandatory and **fail-closed** —
nothing executes unless it passed. Row allocation/weighting does not apply here; the
only computed quantity is the enforced row cap. Full rules:
`.claude/docs/safety_validation.md`.

## 5. Key Constraints (never change or assume)

- **NEVER weaken the validator** to make a query pass. It is SELECT-only,
  single-statement, and whitelist-checked. When editing `Sql/SqlValidator.cs`, add a
  test in `tests/Analyst.Tests/SqlValidatorTests.cs`.
- **NEVER let the app use a write-capable DB account.** Queries run as `analyst_ro`
  (`SELECT` on `gold` only; writes/DDL denied). The DB principal is the real backstop.
- **`config/schema.fnb.json` is the single source of truth** for BOTH the prompt and
  the validator whitelist. Change the schema there, not in code.
- **The seed is deterministic on purpose.** Eval expected results depend on it. Do not
  add `RAND()`/`NEWID()` to `db/02_seed.sql`.
- **Provider is a DI choice** (`IChatClient`); do not hardcode one. `Offline` is the
  default and must keep working with zero API keys.
- **Never commit secrets.** Keys live in user-secrets / env vars, never in the repo.

## 6. Additional Documentation

- `.claude/docs/architecture.md` — projects, request flow, interfaces (REST + MCP), DI
- `.claude/docs/safety_validation.md` — validator rules, ScriptDom AST, defense-in-depth
- `.claude/docs/data_model.md` — `gold` schema, `schema.fnb.json` whitelist, seed
- `.claude/docs/llm_pipeline.md` — providers, prompt builder, generator, summarizer, repair
- `.claude/docs/evaluation.md` — golden set, result-set equivalence, safety suite, gate
- `.claude/docs/deployment.md` — Docker, compose, Azure assets, config/env, tunnel, CI
