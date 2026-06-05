# AI Data Analyst

[![CI](https://github.com/Viluoicode/ai-data-analyst/actions/workflows/ci.yml/badge.svg)](https://github.com/Viluoicode/ai-data-analyst/actions/workflows/ci.yml)

Turns natural-language business questions (Vietnamese or English) into **safe,
read-only SQL**, runs them against an analytics database, and returns the result
rows plus an optional natural-language summary.

The interesting part is not "call an LLM" — it's the **safety and correctness**
layers around it. The model is treated as untrusted: every generated query is
parsed into a real T-SQL AST and validated against a schema whitelist, it executes
through a least-privilege read-only database principal, and accuracy is **measured**
with an evaluation harness rather than assumed.

Built as a ~1-week .NET backend / data-engineering portfolio project.

## Stack

| Concern        | Choice |
|----------------|--------|
| Runtime        | .NET 10 (LTS), Minimal API |
| LLM            | Azure OpenAI via `Microsoft.Extensions.AI` (`IChatClient`) — provider-swappable; a deterministic **offline** provider runs the whole system with no API keys |
| Data source    | Seeded F&B (milk-tea) **gold** star schema in SQL Server 2022. The schema-config is the single source of truth, so re-pointing at a real Medallion *Gold* layer is a config change (see [Point at your own database](#point-at-your-own-database)) |
| SQL validation | `Microsoft.SqlServer.TransactSql.ScriptDom` — a real T-SQL parser, not regex |
| Interfaces     | REST API (+ thin demo page) **and** an MCP server, over one shared core |
| Evaluation     | Golden question set scored by result-set equivalence + a safety suite |

## Architecture

```
 Question (VN/EN)
    │
    ▼
 [1] PromptBuilder ── schema whitelist + few-shot ──►  IChatClient
    │                                                  (Azure OpenAI │ OpenAI │ Ollama │ Offline)
    │  ◄──────────── one repair retry on failure ───── generated SQL { sql, rationale }
    ▼
 [2] SqlValidator  (ScriptDom AST — the core)
       • single statement, SELECT only
       • every table & column ∈ whitelist        → blocks hallucinated names
       • no SELECT…INTO / OPENROWSET / OPENQUERY / TVF / cross-db
       • injects / clamps a TOP row cap
    │   (invalid → refuse with reasons; nothing runs)
    ▼
 [3] SqlExecutor   (read-only principal · command timeout · hard reader row-cap)
    │
    ▼
 [4] Summarizer    (optional · language-matched · sees only a sample of rows)
    │
    ▼
 { status, generatedSql, executedSql, columns, rows, rowCount, summary?, executionMs }
```

### Safety model — defense in depth

The LLM is untrusted; each layer catches what the previous one might miss:

1. **Prompt constraints** — the model is only *told about* whitelisted tables/columns. Helpful, never trusted.
2. **AST validation** — `ScriptDom` parses the SQL; rules above are enforced on the tree. Regex can be defeated by casing/comments/encoding; an AST cannot.
3. **Least-privilege DB principal** — queries run as `analyst_ro`, which has `SELECT` on the `gold` schema **only** and is denied writes/DDL. The real backstop: even a validator bug cannot write or read outside `gold`.
4. **Resource guards** — command timeout + a hard reader-side row cap (enforced even when the `TOP` rewrite is skipped, e.g. for a `UNION`).

The **same** `config/schema.fnb.json` drives both the prompt and the validator whitelist, so the model can only ever be told about objects it is also allowed to touch.

## Project layout

```
src/Analyst.Core    pipeline: schema/whitelist, PromptBuilder, IChatClient providers,
                    SqlValidator, SqlExecutor, Summarizer, AnalystService
src/Analyst.Api     Minimal API + OpenAPI + thin demo page (wwwroot/index.html)
src/Analyst.Mcp     MCP server (ask_data, list_schema) over the same pipeline
tests/Analyst.Tests xUnit: one assertion per validator rule + fail-closed orchestration
tests/Analyst.Eval  golden-set runner -> accuracy + safety scoreboard
db/                 schema, deterministic seed, read-only role
config/             schema.fnb.json — the schema whitelist (single source of truth)
eval/               questions.jsonl (golden set) + safety.jsonl
```

## Quick start

**Prerequisites:** Docker, .NET 10 SDK.

```bash
# 1. Database (SQL Server 2022 in Docker; runs schema -> seed -> read-only role)
docker compose up -d
docker compose logs mssql-init      # expect "DB init complete."

# 2. API (offline provider by default — no API keys needed)
dotnet run --project src/Analyst.Api
# open http://localhost:5184  (port per launchSettings; or set ASPNETCORE_URLS)

# 3. Tests and evaluation
dotnet test tests/Analyst.Tests
dotnet run --project tests/Analyst.Eval -c Release
```

- SQL Server is exposed on host port **11433**.
- Admin: `sa` / `Str0ng!Passw0rd` (dev only). Query principal: `analyst_ro` / `Readonly#Analyst1` (`SELECT` on `gold` only).

### Ask a question

```bash
curl -s -X POST http://localhost:5184/ask \
  -H "Content-Type: application/json" \
  -d '{"question":"Revenue by payment method","includeSummary":true}'
```

```json
{
  "status": "Answered",
  "generatedSql": "SELECT TOP 1000 f.PaymentMethod, SUM(f.LineTotal) AS Revenue ...",
  "executedSql":  "SELECT TOP 1000 f.PaymentMethod, SUM(f.LineTotal) AS Revenue ...",
  "columns": ["PaymentMethod", "Revenue"],
  "rows": [["Cash", 391599000.00], ["Card", 378685400.00], ["EWallet", 275888000.00]],
  "rowCount": 3,
  "summary": "The query returned 3 rows ...",
  "executionMs": 191
}
```

A rejected question returns `200` with `"status": "Refused"`, the `refusalReasons`, and the model's SQL **without executing it**.

> Vietnamese requests must be sent as UTF-8 (e.g. `curl --data-binary @file.json`); inline shell strings on Windows can mangle the encoding.

## Evaluation

```
=== AI Data Analyst — Evaluation (provider: Offline) ===

Text-to-SQL accuracy (result-set equivalence):
  Accuracy: 8/8 (100%)
  By difficulty: easy 3/3, medium 4/4, hard 1/1

Safety suite:
  Malicious blocked: 11/11 (100%)
  Benign allowed:    3/3 (false refusals: 0)

=== Scoreboard ===
  Accuracy : 100%
  Safety   : PASS (block 100%, false-refusals 0)
```

- **Accuracy** compares the pipeline's result set to a reference query's result set, order-insensitive and numeric-normalized. The golden references are written *differently* from the canned offline SQL, so this is a real comparison, not identity.
- **Safety** feeds known-malicious SQL through the validator (must block) plus benign SQL (must pass). Any safety regression fails the run (exit code 1) — usable as a CI gate.
- With the offline provider the canned answers match (a sanity check). To measure a **real model** on the same set, point the harness at any provider (e.g. `ANALYST_PROVIDER=OpenAI` + `ANALYST_OPENAI_BASEURL` for Ollama).

**Real-model run** — same golden set, local `qwen2.5-coder:3b` via Ollama (no cloud):

| Provider | Accuracy | Safety |
|---|---|---|
| Offline (canned, sanity check) | 100% | 100% (11/11 blocked, 0 false-refusals) |
| `qwen2.5-coder:3b` (local, 3B) | **62.5%** | **100%** (11/11 blocked, 0 false-refusals) |

The small model sometimes hallucinates a table (e.g. a non-existent `DimPaymentMethod`) or writes invalid SQL — and the **validator refuses it / the executor catches it every time, so safety is unchanged**. Accuracy is the knob that scales with model quality (a larger 7B+/cloud model scores markedly higher); swapping models is one config line.

## Using a real LLM

The provider is just which `IChatClient` is registered — no code change. Secrets stay out of
source control via user-secrets.

**Azure OpenAI:**

```bash
cd src/Analyst.Api
dotnet user-secrets init
dotnet user-secrets set "Analyst:Provider" "AzureOpenAI"
dotnet user-secrets set "Analyst:Azure:Endpoint" "https://<resource>.openai.azure.com/"
dotnet user-secrets set "Analyst:Azure:DeploymentName" "<chat-deployment>"
dotnet user-secrets set "Analyst:Azure:ApiKey" "<key>"
```

**Local & key-free (Ollama)** — uses Ollama's OpenAI-compatible endpoint, so no cloud account:

```bash
ollama pull qwen2.5-coder:7b
cd src/Analyst.Api && dotnet user-secrets init
dotnet user-secrets set "Analyst:Provider" "OpenAI"
dotnet user-secrets set "Analyst:OpenAI:BaseUrl" "http://localhost:11434/v1"
dotnet user-secrets set "Analyst:OpenAI:Model" "qwen2.5-coder:7b"
dotnet user-secrets set "Analyst:OpenAI:ApiKey" "ollama"
```

The same env vars (`ANALYST_PROVIDER`, `ANALYST_AZURE_*`) point the evaluation harness at a real
model so you can measure its accuracy on the golden set.

## MCP server

Exposes the pipeline as tools an AI agent can call: `ask_data(question, includeSummary)` and `list_schema()`. Communicates over stdio (JSON-RPC).

Register it with an MCP client (e.g. Claude Desktop):

```json
{
  "mcpServers": {
    "ai-data-analyst": {
      "command": "dotnet",
      "args": ["run", "--project", "ABSOLUTE/PATH/TO/src/Analyst.Mcp"]
    }
  }
}
```

Smoke-test it without a client:

```bash
( cat samples/mcp_smoke.jsonl; sleep 8 ) \
  | dotnet run --project src/Analyst.Mcp 2>/dev/null
```

## Point at your own database

Re-pointing at a real Medallion **Gold** layer (or any read-only analytics DB) is three changes, no code:

1. **Schema config** — replace `config/schema.fnb.json` with your real tables/columns, relationships, and a few few-shot examples. This one file is both the prompt context and the validator whitelist.
2. **Connection string** — set `ConnectionStrings:Analyst` to your database, using a **read-only login** scoped to just the analytics schema (mirror `db/03_readonly_role.sql`).
3. **(Optional) provider** — switch to Azure OpenAI for production-grade generation.

The validator, executor, evaluation harness, REST API, and MCP server all read from those, so nothing else changes.

## Data model (F&B demo)

A milk-tea shop chain in the `gold` schema: `FactOrderItem` (one row per order line) joined to `DimDate`, `DimStore`, `DimProduct`, `DimCustomer`. ~12,000 order items across 2024–2025, generated **deterministically** so evaluation results are stable. All amounts are in VND.

## Notes & limitations

- The **offline** provider only emits safe canned SQL, so the live refusal path can't be triggered through it — refusal/repair behavior is covered by the unit tests instead, and is exercised live once a real LLM is configured.
- Column-level whitelisting resolves aliases and is intentionally lenient inside CTEs/derived tables (the underlying base tables are still validated). The table whitelist + read-only principal are the hard security boundaries.
- Dev credentials in `docker-compose.yml` are for local use only.
