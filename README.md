# AI Data Analyst

Turns natural-language business questions (Vietnamese or English) into **safe,
read-only SQL**, runs them against an analytics database, and returns the result
rows plus an optional natural-language summary.

The interesting part is not "call an LLM" — it's the **safety and correctness**
layers around it: the model is treated as untrusted, every generated query is
parsed and validated against a real schema whitelist, and it executes through a
least-privilege read-only database principal. Accuracy is measured with an
evaluation harness.

> Status: **in development** (built as a ~1-week internship portfolio project).

## Stack

| Concern        | Choice |
|----------------|--------|
| Runtime        | .NET 10 (LTS), Minimal API |
| LLM            | Azure OpenAI via `Microsoft.Extensions.AI` (provider-swappable; offline provider for tests) |
| Data source    | Seeded F&B (milk-tea) **gold** star schema in SQL Server 2022; schema-config is the single source of truth, so re-pointing at a real Medallion Gold layer is a config change |
| SQL validation | `Microsoft.SqlServer.TransactSql.ScriptDom` (real T-SQL parser, not regex) |
| Interfaces     | REST API (Swagger) + MCP server over one shared core |
| Evaluation     | Golden question set scored by result-set equivalence + a safety suite |

## Safety model (defense in depth)

1. **Prompt constraints** — schema-scoped instructions (soft; never trusted).
2. **AST validation** — single statement, `SELECT`-only, every table/column must
   be in the whitelist, banned constructs rejected, row limit enforced.
3. **Least-privilege DB principal** — `analyst_ro` can only `SELECT` on the
   `gold` schema and is denied writes/DDL. The real backstop.
4. **Resource guards** — command timeout + hard row cap.

## Project layout

```
src/Analyst.Core    pipeline: schema/whitelist, prompt builder, LLM client, validator, executor, summarizer
src/Analyst.Api     Minimal API + Swagger
src/Analyst.Mcp     MCP server (ask_data, list_schema) over Core
tests/Analyst.Tests unit tests (validator rules) + integration
tests/Analyst.Eval  golden-set runner + scoreboard
db/                 schema, deterministic seed, read-only role
config/             schema.fnb.json — the schema whitelist (single source of truth)
```

## Run the database

```bash
docker compose up -d
docker compose logs mssql-init    # expect "DB init complete."
```

- SQL Server is exposed on host port **11433**.
- Admin: `sa` / `Str0ng!Passw0rd` (dev only).
- App query principal: `analyst_ro` / `Readonly#Analyst1` (SELECT on `gold` only).

App run instructions and the evaluation scoreboard are added as those pieces land.
