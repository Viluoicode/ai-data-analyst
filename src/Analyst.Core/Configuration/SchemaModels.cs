namespace Analyst.Core.Configuration;

/// <summary>
/// Strongly-typed view of <c>schema.fnb.json</c> — the single source of truth that
/// drives BOTH the LLM prompt and the SQL validator's whitelist. Keeping one document
/// for both guarantees the model is only ever told about objects it is also allowed to use.
/// </summary>
public sealed class SchemaConfig
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string DatabaseName { get; init; } = "";
    public string DefaultSchema { get; init; } = "dbo";
    public string Dialect { get; init; } = "TSql";
    public int MaxRows { get; init; } = 1000;
    public int QueryTimeoutSeconds { get; init; } = 30;
    public IReadOnlyList<TableDef> Tables { get; init; } = [];
    public IReadOnlyList<Relationship> Relationships { get; init; } = [];
    public IReadOnlyList<FewShotExample> FewShot { get; init; } = [];
}

public sealed class TableDef
{
    public string Schema { get; init; } = "";
    public string Name { get; init; } = "";
    public string Grain { get; init; } = "";
    public IReadOnlyList<ColumnDef> Columns { get; init; } = [];

    /// <summary>Fully qualified name, e.g. <c>gold.FactOrderItem</c>.</summary>
    public string FullName => $"{Schema}.{Name}";
}

public sealed class ColumnDef
{
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public string Description { get; init; } = "";
}

public sealed class Relationship
{
    public string From { get; init; } = "";
    public string To { get; init; } = "";
}

public sealed class FewShotExample
{
    public string Question { get; init; } = "";
    public string Sql { get; init; } = "";
}
