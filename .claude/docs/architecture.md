# Architecture

## Projects (solution `AiDataAnalyst.slnx`)

| Project | Type | Responsibility |
|---|---|---|
| `src/Analyst.Core` | classlib | The whole pipeline: schema/whitelist, prompt builder, `IChatClient` providers, validator, executor, summarizer, `AnalystService`, DI extension. No web/host dependency. |
| `src/Analyst.Api` | Minimal API | HTTP host: `POST /ask`, `GET /health`, OpenAPI, static demo UI (`wwwroot/index.html`). |
| `src/Analyst.Mcp` | console | MCP stdio server exposing `ask_data` + `list_schema` over the same `AnalystService`. |
| `tests/Analyst.Tests` | xUnit | Validator rules (one assertion per rule) + fail-closed orchestration. DB-free. |
| `tests/Analyst.Eval` | console | Golden-set accuracy + safety scoreboard. Needs the DB. |

Everything goes through `Analyst.Core`. The API and MCP projects are thin adapters.

## Request flow (`AnalystService.AskAsync`)

```
question (string, VN/EN)
  -> ITextToSqlGenerator.GenerateAsync     # Llm/TextToSqlGenerator.cs (IChatClient)
       returns SqlGenerationResult { Sql, Rationale, RawResponse }
  -> if empty            -> Refused (model declined)
  -> SqlValidator.Validate                 # Sql/SqlValidator.cs -> SqlValidationResult
       if invalid -> ONE repair retry (feed errors back via GenerationContext), re-validate
       if still invalid -> Refused (reasons, nothing executed)
  -> SqlExecutor.ExecuteAsync(SafeSql)     # Sql/SqlExecutor.cs (analyst_ro) -> QueryResult
  -> if includeSummary -> ISummarizer.SummarizeAsync
  -> AnalystResult.Answered { GeneratedSql, ExecutedSql, Columns, Rows, RowCount,
                              Truncated, ExecutionMs, Summary?, ReferencedTables }
```

`AnalystStatus` is `Answered | Refused`. Refusals return HTTP 200 with `refusalReasons`
and the model's SQL, but **never execute**.

## Key types and locations (all under `src/Analyst.Core`)

- `AnalystService.cs` — orchestrator (generate → validate → repair → execute → summarize).
- `AnalystResult.cs` — `AnalystResult`, `AnalystStatus`.
- `AnalystCoreOptions.cs` — `AnalystCoreOptions`, `LlmProvider`, `AzureOpenAIOptions`, `OpenAIOptions`.
- `ServiceCollectionExtensions.cs` — `AddAnalystCore(options)` wires every singleton.
- `Configuration/` — `SchemaConfig` model + `SchemaConfigLoader`.
- `Llm/` — `PromptBuilder`, `ITextToSqlGenerator`/`TextToSqlGenerator`, `OfflineChatClient`,
  `ISummarizer`/`LlmSummarizer`/`TemplateSummarizer`, `SqlGenerationResult` (+ `GenerationContext`).
- `Sql/` — `SchemaWhitelist`, `SqlValidator`, `SqlValidationResult`, `ISqlExecutor`/`SqlExecutor`, `QueryResult`.

## Dependency injection

`AddAnalystCore(AnalystCoreOptions)` registers, as singletons: `SchemaConfig` (loaded from
disk), `PromptBuilder`, `ITextToSqlGenerator`, the `IChatClient` (chosen by
`options.Provider`), `ISummarizer` (`TemplateSummarizer` when Offline, else `LlmSummarizer`),
`SqlValidator`, `ISqlExecutor` (`SqlExecutor` with the connection string), `AnalystService`.
The API/MCP hosts bind config into `AnalystCoreOptions` then call this once.

## Interfaces

### REST (`src/Analyst.Api/Program.cs`)
- `POST /ask` — body `{ "question": string, "includeSummary"?: bool }` → `AnalystResult` (enums as strings).
- `GET /health` — `{ status, provider }`.
- `GET /openapi/v1.json` — OpenAPI document. `GET /` — static demo page (`wwwroot/index.html`).
- `$PORT` env overrides the listen port (cloud hosts); container default is `:8080`, local dev `:5184`.

### MCP (`src/Analyst.Mcp`)
See `mcp` details inline: tools `ask_data(question, includeSummary)` and `list_schema()`,
stdio JSON-RPC. Logging is routed to **stderr** (stdout is the protocol channel); content
root is pinned to `AppContext.BaseDirectory` so config resolves regardless of launch CWD.
Tools are declared in `AnalystTools.cs` (`[McpServerToolType]` / `[McpServerTool]`).
