# Data Model

## The `gold` schema (SQL Server 2022)

A milk-tea (F&B) retail star schema. One fact table + four dimensions. Money is in VND.
Date range 2024-01-01 .. 2025-12-31. Defined in `db/01_schema.sql`.

| Table | Grain | Key columns |
|---|---|---|
| `gold.FactOrderItem` | one row per order line | `OrderItemKey` PK, `OrderId`, `DateKey`, `StoreKey`, `ProductKey`, `CustomerKey`, `Quantity`, `UnitPrice`, `DiscountAmount`, `LineTotal`, `PaymentMethod` |
| `gold.DimDate` | one row per day | `DateKey` PK (YYYYMMDD int), `FullDate`, `Year`, `Quarter`, `MonthNum`, `MonthName`, `DayNum`, `DayOfWeekNum` (1=Mon..7=Sun), `DayName`, `IsWeekend` |
| `gold.DimStore` | one per store | `StoreKey` PK, `StoreName`, `City`, `District`, `OpenedDate` |
| `gold.DimProduct` | one per product/size | `ProductKey` PK, `ProductName`, `Category`, `Size`, `BasePrice` |
| `gold.DimCustomer` | one per customer | `CustomerKey` PK, `CustomerName`, `Gender`, `MembershipTier`, `City`, `JoinedDate` |

Fact → dimension FKs on `DateKey` / `StoreKey` / `ProductKey` / `CustomerKey`.

Conventions the prompt/eval rely on: **revenue = `SUM(LineTotal)`**; **orders =
`COUNT(DISTINCT OrderId)`**; time filters via a join to `gold.DimDate`. `PaymentMethod` ∈
{`Cash`,`Card`,`EWallet`}; `MembershipTier` ∈ {`Bronze`,`Silver`,`Gold`}.

## Seed (`db/02_seed.sql`) — DETERMINISTIC

Row counts: DimStore 6, DimProduct 18, DimCustomer 300, DimDate 731, **FactOrderItem 12,000**.
Generated with `GENERATE_SERIES` + modular arithmetic (no `RAND()`/`NEWID()`), so the dataset
is identical on every load and the eval golden results stay stable. **Do not introduce
randomness.** Multipliers were chosen coprime with the dimension sizes for even spread.

## The whitelist — `config/schema.fnb.json` (single source of truth)

Deserialized into `SchemaConfig` (`Configuration/SchemaModels.cs`) by `SchemaConfigLoader`
(`JsonSerializerDefaults.Web`, comments + trailing commas allowed). Shape:

```
{ name, description, databaseName, defaultSchema, dialect, maxRows, queryTimeoutSeconds,
  tables: [ { schema, name, grain, columns: [ { name, type, description } ] } ],
  relationships: [ { from, to } ],
  fewShot: [ { question, sql } ] }
```

This one file drives BOTH:
- the **prompt** (`PromptBuilder` renders tables/columns/relationships/rules + few-shot), and
- the **validator** (`SchemaWhitelist` is built from `SchemaConfig.Tables`).

So the model can only be told about objects it is also allowed to use. To expose a new
table/column: add it here. `maxRows` here is the enforced `TOP` cap; `queryTimeoutSeconds`
is the command timeout.

## Read-only principal (`db/03_readonly_role.sql`)

`analyst_ro`: `GRANT SELECT ON SCHEMA::gold`; `DENY INSERT, UPDATE, DELETE, EXECUTE, ALTER`;
`DEFAULT_SCHEMA = gold` (so an LLM that omits the `gold.` prefix still resolves). It is NOT
in `db_datareader` (that would expose every schema). The app connects as this principal; SQL
admin (`sa`) is used only by the init container. Dev passwords in these scripts /
`docker-compose.yml` are local-only.

## Pointing at a different database (Gold-layer swap)
Three changes, no code: (1) replace `config/schema.fnb.json` with the real tables/columns;
(2) set `ConnectionStrings__Analyst` to the target DB with a read-only login; (3) optionally
switch the provider. See `deployment.md` and `db/03_readonly_role.sql` (or
`deploy/azure/03_role.sql` for a contained Azure SQL user).
