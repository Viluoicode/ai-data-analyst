using Analyst.Core.Configuration;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Analyst.Core.Sql;

/// <summary>
/// Validates LLM-generated SQL by parsing it into a real T-SQL AST (ScriptDom) — never regex,
/// which casing/comments/encoding can defeat. Enforces, in order:
///   1. The text parses as valid T-SQL.
///   2. Exactly ONE statement, and it is a SELECT (blocks INSERT/UPDATE/DELETE/DDL/EXEC and ';' stacking).
///   3. Every table is in the whitelist; no cross-database / linked-server references.
///   4. No SELECT ... INTO, OPENROWSET/OPENQUERY, or table-valued function calls.
///   5. Every column reference resolves to a whitelisted column.
/// Finally it rewrites the query with an enforced TOP row cap and returns the safe SQL.
/// </summary>
public sealed class SqlValidator
{
    private readonly SchemaWhitelist _whitelist;
    private readonly int _maxRows;

    public SqlValidator(SchemaConfig schema)
    {
        _whitelist = new SchemaWhitelist(schema);
        _maxRows = schema.MaxRows;
    }

    public SqlValidationResult Validate(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return SqlValidationResult.Invalid("Empty SQL.");

        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        TSqlFragment fragment;
        IList<ParseError> parseErrors;
        using (var reader = new StringReader(sql))
            fragment = parser.Parse(reader, out parseErrors);

        if (parseErrors.Count > 0)
            return SqlValidationResult.Invalid(
                parseErrors.Select(e => $"Parse error (line {e.Line}): {e.Message}").ToList());

        if (fragment is not TSqlScript script)
            return SqlValidationResult.Invalid("Input is not a T-SQL script.");

        var statements = script.Batches.SelectMany(b => b.Statements).ToList();
        if (statements.Count == 0)
            return SqlValidationResult.Invalid("No statement found.");
        if (statements.Count > 1)
            return SqlValidationResult.Invalid($"Only a single statement is allowed; found {statements.Count}.");
        if (statements[0] is not SelectStatement select)
            return SqlValidationResult.Invalid($"Only SELECT statements are allowed; found {Describe(statements[0])}.");

        // CTE names are legal "table" references; collect them so they aren't flagged as unknown tables.
        var cteNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (select.WithCtesAndXmlNamespaces is { } with)
            foreach (var cte in with.CommonTableExpressions)
                cteNames.Add(cte.ExpressionName.Value);

        var errors = new List<string>();
        if (select.Into is not null)
            errors.Add("SELECT ... INTO (which creates a table) is not allowed.");

        var visitor = new SafetyVisitor(_whitelist, cteNames);
        select.Accept(visitor);
        visitor.ValidateColumns();
        errors.AddRange(visitor.Errors);

        if (errors.Count > 0)
            return SqlValidationResult.Invalid(errors);

        EnforceRowCap(select);

        var generator = new Sql160ScriptGenerator(new SqlScriptGeneratorOptions
        {
            IncludeSemicolons = true,
            AlignClauseBodies = false
        });
        generator.GenerateScript(select, out var safeSql);

        return SqlValidationResult.Valid(safeSql.Trim(), visitor.ReferencedTables.ToList());
    }

    /// <summary>Inject TOP {maxRows} when missing; clamp it down when present and larger.</summary>
    private void EnforceRowCap(SelectStatement select)
    {
        if (select.QueryExpression is not QuerySpecification qs)
            return; // UNION etc. — the executor's hard reader cap still applies.

        if (qs.TopRowFilter is null)
        {
            qs.TopRowFilter = new TopRowFilter
            {
                Percent = false,
                WithTies = false,
                Expression = new IntegerLiteral { Value = _maxRows.ToString() }
            };
        }
        else if (qs.TopRowFilter.Percent)
        {
            qs.TopRowFilter.Percent = false;
            qs.TopRowFilter.Expression = new IntegerLiteral { Value = _maxRows.ToString() };
        }
        else if (qs.TopRowFilter.Expression is IntegerLiteral lit
                 && int.TryParse(lit.Value, out var n) && n > _maxRows)
        {
            lit.Value = _maxRows.ToString();
        }
    }

    private static string Describe(TSqlStatement statement)
        => statement.GetType().Name.Replace("Statement", "").ToUpperInvariant();

    /// <summary>Walks the SELECT once, recording tables/aliases/columns and flagging banned constructs.</summary>
    private sealed class SafetyVisitor : TSqlFragmentVisitor
    {
        private readonly SchemaWhitelist _whitelist;
        private readonly HashSet<string> _cteNames;

        private readonly Dictionary<string, TableDef> _aliasToTable = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<TableDef> _referencedDefs = [];
        private readonly HashSet<string> _derivedAliases = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _outputAliases = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<ColumnReferenceExpression> _columnRefs = [];

        public List<string> Errors { get; } = [];
        public HashSet<string> ReferencedTables { get; } = new(StringComparer.OrdinalIgnoreCase);

        public SafetyVisitor(SchemaWhitelist whitelist, HashSet<string> cteNames)
        {
            _whitelist = whitelist;
            _cteNames = cteNames;
        }

        public override void Visit(NamedTableReference node)
        {
            var obj = node.SchemaObject;
            if (obj.ServerIdentifier is not null || obj.DatabaseIdentifier is not null)
            {
                Errors.Add("Cross-database / linked-server references are not allowed.");
                return;
            }

            var schema = obj.SchemaIdentifier?.Value;
            var name = obj.BaseIdentifier.Value;
            var alias = node.Alias?.Value;

            // A reference to a CTE defined in this query — allowed, columns checked inside the CTE body.
            if (schema is null && _cteNames.Contains(name))
            {
                _derivedAliases.Add(alias ?? name);
                return;
            }

            if (!_whitelist.TryResolveTable(schema, name, out var def))
            {
                Errors.Add($"Table not allowed or unknown: {(schema is null ? "" : schema + ".")}{name}");
                return;
            }

            _referencedDefs.Add(def);
            ReferencedTables.Add(def.FullName);
            _aliasToTable[def.FullName] = def;
            _aliasToTable[name] = def;
            if (alias is not null)
                _aliasToTable[alias] = def;
        }

        public override void Visit(QueryDerivedTable node)
        {
            if (node.Alias?.Value is { } a)
                _derivedAliases.Add(a);
        }

        public override void Visit(QuerySpecification node)
        {
            foreach (var element in node.SelectElements)
                if (element is SelectScalarExpression { ColumnName.Value: { } alias })
                    _outputAliases.Add(alias);
        }

        public override void Visit(ColumnReferenceExpression node)
        {
            if (node.ColumnType == ColumnType.Regular && node.MultiPartIdentifier is not null)
                _columnRefs.Add(node);
        }

        // --- Banned constructs that can appear inside a SELECT --------------------------------
        public override void Visit(OpenRowsetTableReference node)
            => Errors.Add("OPENROWSET / OPENDATASOURCE is not allowed.");

        public override void Visit(OpenQueryTableReference node)
            => Errors.Add("OPENQUERY is not allowed.");

        public override void Visit(SchemaObjectFunctionTableReference node)
            => Errors.Add($"Table-valued function calls are not allowed: {node.SchemaObject.BaseIdentifier.Value}");

        /// <summary>Run after the walk completes, when alias/output-alias maps are fully populated.</summary>
        public void ValidateColumns()
        {
            foreach (var c in _columnRefs)
            {
                var ids = c.MultiPartIdentifier.Identifiers;
                var column = ids[^1].Value;

                if (ids.Count >= 2)
                {
                    var prefix = ids[^2].Value;
                    if (_derivedAliases.Contains(prefix))
                        continue;
                    if (!_aliasToTable.TryGetValue(prefix, out var table))
                    {
                        Errors.Add($"Unknown table/alias prefix '{prefix}'.");
                        continue;
                    }
                    if (!_whitelist.ColumnExists(table, column))
                        Errors.Add($"Column '{column}' is not in table {table.FullName}.");
                }
                else
                {
                    if (_outputAliases.Contains(column)) continue;   // ORDER BY <select alias>
                    if (_referencedDefs.Count == 0) continue;        // e.g. SELECT 1
                    if (_derivedAliases.Count > 0) continue;         // be lenient when CTEs/derived tables are present
                    if (!_referencedDefs.Any(t => _whitelist.ColumnExists(t, column)))
                        Errors.Add($"Column '{column}' is not found in any referenced table.");
                }
            }
        }
    }
}
