# LLM Pipeline

The model is an implementation detail behind `Microsoft.Extensions.AI`'s `IChatClient`.
Swapping providers is a DI/config change, never a code change.

## Providers (`LlmProvider` in `AnalystCoreOptions.cs`)

| Value | `IChatClient` | Notes |
|---|---|---|
| `Offline` (default) | `OfflineChatClient` | Deterministic canned SQL keyed by normalized question; benign fallback otherwise. **Zero API keys.** Lets validator/executor/eval run with no provider. |
| `AzureOpenAI` | `AzureOpenAIClient(...).GetChatClient(deployment).AsIChatClient()` | Needs `Analyst__Azure__Endpoint`/`DeploymentName`/`ApiKey`. |
| `OpenAI` | `OpenAIClient(ApiKeyCredential, OpenAIClientOptions{ Endpoint })` | `Analyst__OpenAI__ApiKey` + `Model`. Set `Analyst__OpenAI__BaseUrl` to target an **OpenAI-compatible** server — local **Ollama** (`http://localhost:11434/v1`), Groq, Gemini, etc. |

Wiring is in `ServiceCollectionExtensions.BuildChatClient`. `Offline` must always keep working.

## Prompt construction (`Llm/PromptBuilder.cs`)

`SystemPrompt` is rendered once from `SchemaConfig`: database name, description, the
allowed tables/columns with descriptions, relationships, hard rules (single SELECT, only
listed tables/columns, schema-qualify, bracket reserved words like `[Year]`, always `TOP`,
revenue/orders conventions, JSON output contract). `BuildMessages(question, repair?)` =
system + few-shot pairs (from `SchemaConfig.FewShot`) + the user question. When `repair` is
set, it appends the rejected SQL + validator errors so the model can correct itself.

## Generation (`Llm/TextToSqlGenerator.cs`)

`GenerateAsync(question, repair?, ct)` calls `IChatClient.GetResponseAsync` with
`ChatOptions { Temperature = 0f, ResponseFormat = ChatResponseFormat.Json }`. `Parse` reads
`{ "sql", "rationale" }`; if the text isn't a JSON object it strips ``` code fences and
treats the body as raw SQL. Returns `SqlGenerationResult` (empty `Sql` ⇒ model declined ⇒
the pipeline refuses).

## Repair retry (`AnalystService`)

On the first validation failure, the generator is called again with
`GenerationContext(previousSql, errors)`; the repaired SQL is re-validated. Exactly one
retry. Offline never triggers it (canned SQL is already valid); it matters for real models.

## Summary (`Llm/ISummarizer.cs`)

Opt-in via `includeSummary`. `LlmSummarizer` sends only a sample (≤20 rows) to the chat model
and instructs it to answer **in the question's language**. `TemplateSummarizer` (used in
Offline mode) builds a deterministic summary and detects Vietnamese via diacritic markers.
DI picks `TemplateSummarizer` when `Provider == Offline`, else `LlmSummarizer`.

## Configuration keys

`appsettings.json` → `ConnectionStrings:Analyst`, `Analyst:Provider`, `Analyst:Azure:*`,
`Analyst:OpenAI:{ApiKey,Model,BaseUrl}`, `Analyst:SchemaConfigPath`. As env vars use `__`:
`Analyst__Provider`, `Analyst__OpenAI__BaseUrl`, etc. The eval runner also reads
`ANALYST_PROVIDER`, `ANALYST_OPENAI_BASEURL/MODEL/APIKEY`, `ANALYST_AZURE_*`,
`ANALYST_CONNSTRING`. Keys go in user-secrets (Development) or env vars — never the repo.

## Local key-free real model (Ollama)
`Provider=OpenAI`, `OpenAI:BaseUrl=http://localhost:11434/v1`, `OpenAI:Model=qwen2.5-coder:3b`,
`OpenAI:ApiKey=ollama`. Verifies the real NL→SQL path with no cloud account.
