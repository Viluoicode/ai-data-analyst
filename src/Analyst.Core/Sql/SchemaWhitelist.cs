using Analyst.Core.Configuration;

namespace Analyst.Core.Sql;

/// <summary>
/// Case-insensitive allow-list of tables and their columns, built from the same
/// <see cref="SchemaConfig"/> that feeds the prompt. The validator resolves every table/column
/// reference against this — anything not here is rejected (kills hallucinated names).
/// </summary>
public sealed class SchemaWhitelist
{
    private readonly Dictionary<string, TableDef> _byFullName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<TableDef>> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _columnsByFullName = new(StringComparer.OrdinalIgnoreCase);

    public SchemaWhitelist(SchemaConfig schema)
    {
        foreach (var t in schema.Tables)
        {
            _byFullName[t.FullName] = t;

            if (!_byName.TryGetValue(t.Name, out var list))
                _byName[t.Name] = list = [];
            list.Add(t);

            _columnsByFullName[t.FullName] =
                new HashSet<string>(t.Columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Resolve a table by (optional) schema + name. Unqualified names resolve only if unambiguous.</summary>
    public bool TryResolveTable(string? schema, string name, out TableDef table)
    {
        if (!string.IsNullOrEmpty(schema))
            return _byFullName.TryGetValue($"{schema}.{name}", out table!);

        if (_byName.TryGetValue(name, out var list) && list.Count == 1)
        {
            table = list[0];
            return true;
        }

        table = null!;
        return false;
    }

    public bool ColumnExists(TableDef table, string column)
        => _columnsByFullName.TryGetValue(table.FullName, out var cols) && cols.Contains(column);
}
