# AI Data Analyst — Handoff Document

> **Mục đích file này:** Đem sang session mới của project pipeline để gộp AI Data Analyst
> vào thành tầng truy vấn NL→SQL trên Gold layer. File chứa MỌI THỨ cần biết: kiến trúc,
> code, cách gộp, và những gì không được thay đổi.

---

## 1. Project này là gì

Một .NET backend service chuyển câu hỏi tiếng Việt/Anh thành **SQL chỉ-đọc an toàn**,
chạy trên SQL Server analytics schema (Gold layer), trả về kết quả + tóm tắt NL.

Điểm cốt lõi **không phải gọi LLM** — mà là lớp an toàn/đo lường bao quanh nó:
mọi SQL sinh ra đều bị parse thành AST (T-SQL parser thật, không regex), kiểm tra
whitelist bảng/cột, và chạy qua tài khoản DB chỉ-đọc. Độ chính xác được đo bằng
evaluation harness, không assume.

**GitHub:** https://github.com/Viluoicode/ai-data-analyst
**Trạng thái:** hoàn chỉnh, 12 commits, CI xanh, 27 tests pass, eval 100% safety.

---

## 2. Vị trí trong data platform

```
Source → [Pipeline: Bronze → Silver → Gold] → AI Data Analyst → Người dùng
              (project pipeline)                (project này)     hỏi tự do
```

Pipeline **tạo** data sạch trong Gold layer. AI Analyst **mở khóa truy cập** cho người
không biết SQL. Cả hai đọc/ghi cùng một Gold schema. Không thay thế nhau — bổ sung.

---

## 3. Tech stack

| Thành phần | Công nghệ | Version |
|---|---|---|
| Runtime | .NET 10 (LTS), C#, ASP.NET Core Minimal API | `net10.0` |
| Solution | `AiDataAnalyst.slnx` | |
| LLM abstraction | `Microsoft.Extensions.AI` (`IChatClient`) | 10.6.0 |
| Providers | Azure OpenAI (`Azure.AI.OpenAI`), OpenAI/Ollama (`Microsoft.Extensions.AI.OpenAI`), Offline (canned, no keys) | 2.1.0 / 10.6.0 |
| SQL validation | `Microsoft.SqlServer.TransactSql.ScriptDom` (real T-SQL AST parser) | 180.18.1 |
| Data access | `Microsoft.Data.SqlClient` | 7.0.1 |
| MCP server | `ModelContextProtocol` (stdio) | 1.4.0 |
| Database | SQL Server 2022 (Docker) | |
| Tests | xUnit | 2.9.3 |
| CI | GitHub Actions (build + test + Dockerized eval) | |

---

## 4. Cấu trúc project

```
src/
  Analyst.Core/          ← Toàn bộ pipeline (không phụ thuộc web/host)
    Configuration/         SchemaConfig, SchemaConfigLoader
    Llm/                   PromptBuilder, ITextToSqlGenerator, TextToSqlGenerator,
                           OfflineChatClient, ISummarizer, LlmSummarizer,
                           TemplateSummarizer, SqlGenerationResult, GenerationContext
    Sql/                   SchemaWhitelist, SqlValidator, SqlValidationResult,
                           ISqlExecutor, SqlExecutor, QueryResult
    AnalystService.cs      Orchestrator (generate → validate → repair → execute → summarize)
    AnalystResult.cs       AnalystResult, AnalystStatus (Answered | Refused)
    AnalystCoreOptions.cs  LlmProvider enum, AzureOpenAIOptions, OpenAIOptions
    ServiceCollectionExtensions.cs  AddAnalystCore(options) — wires everything

  Analyst.Api/           ← REST API host
    Program.cs             POST /ask, GET /health, static files, OpenAPI
    wwwroot/index.html     Demo UI (light/dark, SQL syntax highlight, summary)
    appsettings.json       ConnectionStrings:Analyst, Analyst:Provider, Analyst:OpenAI:*

  Analyst.Mcp/           ← MCP server (stdio, cho AI agent)
    Program.cs             Host + StdioServerTransport
    AnalystTools.cs        ask_data(question, includeSummary), list_schema()

tests/
  Analyst.Tests/         ← 27 unit tests (DB-free)
    SqlValidatorTests.cs   17 test methods — một assertion mỗi rule
    AnalystServiceTests.cs 4 test methods — fail-closed, repair, summary

  Analyst.Eval/          ← Evaluation harness (cần DB chạy)
    Program.cs             Golden set + safety suite → scoreboard

config/
  schema.fnb.json        ← SINGLE SOURCE OF TRUTH cho prompt + validator whitelist

eval/
  questions.jsonl          8 golden questions (VN+EN, tagged difficulty)
  safety.jsonl             11 malicious + 3 benign test cases

db/
  01_schema.sql            Gold star schema (5 tables)
  02_seed.sql              Deterministic seed (12,000 order items)
  03_readonly_role.sql     analyst_ro (SELECT on gold only, writes denied)

deploy/azure/              Azure SQL + Container Apps deploy scripts
Dockerfile                 Multi-stage build (sdk → aspnet runtime)
docker-compose.yml         mssql + mssql-init + api
.github/workflows/ci.yml   Build + test + Dockerized eval gate
```

**Tổng code chính:** ~1,443 dòng (14 files). Compact.

---

## 5. Luồng xử lý chính

```
AnalystService.AskAsync(question, includeSummary)
  │
  ├─ ITextToSqlGenerator.GenerateAsync(question)
  │    IChatClient (Azure/OpenAI/Ollama/Offline) → temperature 0, JSON output
  │    → SqlGenerationResult { Sql, Rationale }
  │    Nếu Sql rỗng → Refused (model declined)
  │
  ├─ SqlValidator.Validate(sql)
  │    TSql160Parser parse → AST
  │    Kiểm tra: 1 statement, SELECT only, whitelist tables/columns,
  │    no SELECT..INTO/OPENROWSET/OPENQUERY/TVF/cross-DB, inject TOP cap
  │    Nếu fail → 1 repair retry (feed errors back to model) → re-validate
  │    Nếu vẫn fail → Refused (reasons, KHÔNG CHẠY)
  │
  ├─ SqlExecutor.ExecuteAsync(safeSql)
  │    Connection = analyst_ro (SELECT on gold only)
  │    Command timeout + hard reader row cap
  │    → QueryResult { Columns, Rows, RowCount, Truncated, ExecutionMs }
  │
  ├─ ISummarizer.SummarizeAsync (nếu includeSummary)
  │    Language-matched (VN question → VN summary)
  │
  └─ → AnalystResult { Status, GeneratedSql, ExecutedSql, Columns, Rows,
                        RowCount, Summary?, ReferencedTables, RefusalReasons? }
```

---

## 6. Mô hình an toàn — 4 lớp defense-in-depth

| Lớp | Cơ chế | Vị trí |
|---|---|---|
| 1. Prompt | Chỉ "kể" cho model bảng/cột được phép | PromptBuilder.cs |
| 2. **AST Validator** | ScriptDom parse SQL → kiểm tra tree, whitelist | SqlValidator.cs |
| 3. **DB principal** | `analyst_ro`: SELECT on gold only; DENY writes/DDL | 03_readonly_role.sql |
| 4. Resource guard | Command timeout + reader row cap | SqlExecutor.cs |

**Fail-closed:** không gì chạy trừ khi validator pass. Unit test chứng minh executor
KHÔNG BAO GIỜ được gọi khi SQL invalid (`FakeExecutor.Calls == 0`).

---

## 7. Schema whitelist — single source of truth

`config/schema.fnb.json` drives BOTH prompt AND validator. Cấu trúc:

```json
{
  "name": "fnb-gold",
  "description": "...",
  "databaseName": "AnalystDB",
  "defaultSchema": "gold",
  "maxRows": 1000,
  "queryTimeoutSeconds": 30,
  "tables": [
    {
      "schema": "gold", "name": "FactOrderItem", "grain": "...",
      "columns": [
        { "name": "OrderItemKey", "type": "bigint", "description": "..." },
        ...
      ]
    }
  ],
  "relationships": [
    { "from": "gold.FactOrderItem.DateKey", "to": "gold.DimDate.DateKey" }
  ],
  "fewShot": [
    { "question": "What was total revenue in 2024?", "sql": "SELECT ..." }
  ]
}
```

**Để trỏ sang Gold layer của pipeline: tạo file mới (e.g. `schema.ecommerce.json`),
set `Analyst:SchemaConfigPath` trỏ tới nó. Không sửa code.**

---

## 8. Cách gộp vào pipeline project

### Bước 1 — Copy project vào monorepo (hoặc giữ riêng, reference nhau)

```bash
# Option A: copy thư mục vào pipeline repo
cp -r /path/to/ai-data-analyst/src  /path/to/pipeline/ai-analyst/src
cp -r /path/to/ai-data-analyst/tests /path/to/pipeline/ai-analyst/tests
cp    /path/to/ai-data-analyst/config/schema.fnb.json /path/to/pipeline/config/

# Option B: git submodule (giữ repo riêng)
git submodule add https://github.com/Viluoicode/ai-data-analyst ai-analyst
```

### Bước 2 — Tạo schema config cho Gold layer e-commerce

Chạy trên DB pipeline:
```sql
SELECT TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME, DATA_TYPE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = 'gold'
ORDER BY TABLE_NAME, ORDINAL_POSITION;
```
→ Đưa kết quả vào `config/schema.ecommerce.json` theo format ở mục 7.

### Bước 3 — Tạo read-only user cho pipeline DB

```sql
CREATE USER analyst_ro WITH PASSWORD = '...', DEFAULT_SCHEMA = gold;
GRANT SELECT ON SCHEMA::gold TO analyst_ro;
DENY INSERT, UPDATE, DELETE, EXECUTE, ALTER ON SCHEMA::gold TO analyst_ro;
```

### Bước 4 — Cấu hình connection string

```bash
# appsettings hoặc env var:
ConnectionStrings__Analyst = "Server=...;Database=PipelineDB;User Id=analyst_ro;Password=...;..."
Analyst__SchemaConfigPath  = "config/schema.ecommerce.json"
```

### Bước 5 — Thêm vào docker-compose của pipeline

```yaml
analyst-api:
  build:
    context: ./ai-analyst   # hoặc đường dẫn tới Dockerfile
    dockerfile: Dockerfile
  depends_on:
    pipeline-db:
      condition: service_healthy
  environment:
    ConnectionStrings__Analyst: "Server=pipeline-db,1433;Database=...;User Id=analyst_ro;..."
    Analyst__SchemaConfigPath: "config/schema.ecommerce.json"
    Analyst__Provider: "Offline"   # đổi sang OpenAI khi có key
  ports:
    - "8080:8080"
```

### Bước 6 — Viết few-shot examples + golden eval set cho e-commerce data

Thêm 3-5 cặp `{ question, sql }` vào `fewShot` trong schema config. Tạo
`eval/questions.ecommerce.jsonl` cho eval harness.

---

## 9. Kết quả đã đo

| Provider | Accuracy | Safety | Latency |
|---|---|---|---|
| Offline (canned) | 100% (8/8) | 100% (block 11/11, false-refusals 0) | avg 82ms |
| qwen2.5-coder:3b (Ollama local) | 62.5% (5/8) | **100%** | avg 127ms |

Safety **không phụ thuộc model** — validator + DB principal chặn mọi truy vấn nguy hiểm
bất kể model giỏi hay dở. Accuracy scales với model quality (GPT-4o-mini ~90-100%).

---

## 10. Dev commands

```bash
# DB (SQL Server 2022 Docker)
docker compose up -d
docker compose logs mssql-init   # "DB init complete."

# Build
dotnet build AiDataAnalyst.slnx -c Release

# Run API (offline, port 5184 dev / 8080 container)
dotnet run --project src/Analyst.Api

# Tests (27, DB-free)
dotnet test tests/Analyst.Tests

# Eval scoreboard (needs DB)
dotnet run --project tests/Analyst.Eval -c Release

# Full stack containerized
docker compose up -d --build api

# Ollama (local, key-free)
dotnet user-secrets set "Analyst:Provider" "OpenAI" --project src/Analyst.Api
dotnet user-secrets set "Analyst:OpenAI:BaseUrl" "http://localhost:11434/v1" --project src/Analyst.Api
dotnet user-secrets set "Analyst:OpenAI:Model" "qwen2.5-coder:3b" --project src/Analyst.Api
dotnet user-secrets set "Analyst:OpenAI:ApiKey" "ollama" --project src/Analyst.Api
```

---

## 11. Key constraints (KHÔNG ĐƯỢC thay đổi khi gộp)

1. **KHÔNG bao giờ yếu hóa validator** — SELECT-only, single-statement, whitelist.
   Khi sửa `SqlValidator.cs`, thêm test trong `SqlValidatorTests.cs`.
2. **KHÔNG cho app dùng tài khoản DB có quyền ghi.** `analyst_ro` chỉ SELECT trên gold.
3. **`schema.*.json` là source of truth duy nhất** cho prompt + validator. Đổi schema ở
   đó, không hardcode trong code.
4. **Seed phải deterministic.** Eval results phụ thuộc vào nó. Không dùng RAND()/NEWID().
5. **Provider là DI choice** (`IChatClient`). Offline phải luôn hoạt động (zero keys).
6. **Không commit secrets.** Keys sống trong user-secrets / env vars.

---

## 12. Interfaces

### REST API
- `POST /ask` — `{ "question": string, "includeSummary"?: bool }` → `AnalystResult`
- `GET /health` — `{ status, provider }`
- `GET /` — demo page. `GET /openapi/v1.json` — OpenAPI spec.

### MCP Server (stdio JSON-RPC)
- Tool `ask_data(question, includeSummary)` → JSON AnalystResult
- Tool `list_schema()` → text mô tả bảng/cột

---

## 13. Gợi ý cách viết CV (gộp thành 1 project)

> **E-Commerce Data Platform** — *Python, .NET 10, SQL Server, LLM, Docker*
>
> End-to-end data platform: ingestion pipeline (Medallion Bronze→Silver→Gold) +
> AI query layer converting natural-language questions (VN/EN) to safe, read-only SQL.
> Defense-in-depth: ScriptDom AST validation, schema whitelist, least-privilege DB →
> **100% malicious-query block rate**. Evaluation harness (result-set equivalence),
> 27 unit tests, CI/CD (GitHub Actions), Docker, MCP server for AI agents.

---

*File này tự đủ — không cần đọc thêm gì khác để hiểu project và gộp vào pipeline.*
