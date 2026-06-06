# Safety & Validation

The LLM is **untrusted**. Four independent layers; each catches what the previous might miss.

1. **Prompt constraints** (soft) — the model is only told about whitelisted tables/columns
   (`PromptBuilder`). Helpful, never trusted.
2. **AST validation** (hard, app-level) — `Sql/SqlValidator.cs`. The centerpiece.
3. **Least-privilege DB principal** (hard, DB-level) — `analyst_ro` can only `SELECT` on
   the `gold` schema; writes/DDL are denied. The real backstop (see `data_model.md`).
4. **Resource guards** — command timeout + a hard reader-side row cap in `SqlExecutor`,
   enforced even when the `TOP` rewrite is skipped (e.g. a `UNION`).

## `SqlValidator.Validate(sql)` → `SqlValidationResult`

Uses `TSql160Parser` (SQL Server 2022 grammar). NEVER replace this with regex.

Sequence:
1. Parse. Any `ParseError` → invalid.
2. Flatten `TSqlScript.Batches[].Statements`. Require **exactly one** statement, and it
   must be a `SelectStatement` (blocks INSERT/UPDATE/DELETE/MERGE/DDL/EXEC and `;` stacking).
3. Reject `SelectStatement.Into` (`SELECT ... INTO`, which creates a table).
4. Collect CTE names (`WithCtesAndXmlNamespaces`) so CTE references aren't flagged as unknown tables.
5. Walk with the private `SafetyVisitor` (`TSqlFragmentVisitor`), then `ValidateColumns()`.
6. If clean, `EnforceRowCap`, regenerate SQL with `Sql160ScriptGenerator`, return `SafeSql`.

### `SafetyVisitor` checks
- `NamedTableReference` — reject 3/4-part names (cross-DB / linked server). Resolve
  schema+name via `SchemaWhitelist`; unknown table → error. Record alias→table for column checks.
- Banned table references → error: `OpenRowsetTableReference`, `OpenQueryTableReference`,
  `SchemaObjectFunctionTableReference` (table-valued functions).
- `QueryDerivedTable` / CTE names → recorded as "derived" aliases (columns under them are
  accepted leniently; their **inner** base tables are still validated).
- `ColumnReferenceExpression` (regular only) collected, then validated:
  - 2+ parts (`alias.col`): prefix must resolve to a whitelisted table; column must exist there.
  - 1 part (`col`): accepted if it is a SELECT output alias, OR exists in any referenced
    table; lenient when CTEs/derived tables are present.

### Row cap (`EnforceRowCap`)
If the outer `QuerySpecification` has no `TopRowFilter`, inject `TOP {MaxRows}`. If `TOP` is
`PERCENT` or a literal larger than `MaxRows`, clamp it. `MaxRows` comes from
`schema.fnb.json` (default 1000). For `UNION` etc., the executor's reader cap applies instead.

## Strict vs lenient (by design)
- **Strict** (security + correctness): table whitelist, single-SELECT, banned constructs,
  cross-DB block. The table whitelist + read-only principal are the hard security boundary.
- **Lenient** (avoid false rejects): single-part columns and columns inside CTEs/derived
  tables. This cannot widen data access because the table whitelist already constrains it.

## Adding or changing a rule
1. Edit `Sql/SqlValidator.cs` (statement-level checks in `Validate`; node-level in `SafetyVisitor`).
2. Add a `[Fact]`/`[Theory]` to `tests/Analyst.Tests/SqlValidatorTests.cs` — one assertion per
   rule, both a passing and a failing case.
3. Add an adversarial line to `eval/safety.jsonl` if it is a new attack class.
4. Run `dotnet test tests/Analyst.Tests` and `dotnet run --project tests/Analyst.Eval -c Release`.

## Fail-closed guarantee
`AnalystService` only calls the executor when validation passed. `AnalystServiceTests`
asserts that malicious SQL is refused and the executor is **never reached** (`Calls == 0`).
Do not add a code path that executes unvalidated SQL.
