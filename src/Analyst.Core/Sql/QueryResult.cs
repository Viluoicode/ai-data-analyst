namespace Analyst.Core.Sql;

/// <summary>Tabular result of executing a validated query.</summary>
/// <param name="Columns">Column names in order.</param>
/// <param name="Rows">Row values; nulls are represented as <c>null</c>.</param>
/// <param name="RowCount">Number of rows returned (after the cap).</param>
/// <param name="Truncated">True if more rows existed but were cut off by the row cap.</param>
/// <param name="ExecutionMs">Server round-trip time in milliseconds.</param>
public sealed record QueryResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<object?[]> Rows,
    int RowCount,
    bool Truncated,
    long ExecutionMs);
