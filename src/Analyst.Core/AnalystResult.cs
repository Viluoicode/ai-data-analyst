using Analyst.Core.Sql;

namespace Analyst.Core;

public enum AnalystStatus
{
    /// <summary>SQL was generated, passed validation, and executed.</summary>
    Answered,
    /// <summary>The question was refused: the model declined or validation rejected the SQL. Nothing executed.</summary>
    Refused
}

/// <summary>End-to-end outcome of asking a natural-language question.</summary>
public sealed class AnalystResult
{
    public required AnalystStatus Status { get; init; }
    public required string Question { get; init; }

    /// <summary>The raw SQL the model produced (surfaced for transparency, even on refusal).</summary>
    public string? GeneratedSql { get; init; }

    /// <summary>The validated, row-capped SQL that actually ran (only when Answered).</summary>
    public string? ExecutedSql { get; init; }

    public IReadOnlyList<string>? Columns { get; init; }
    public IReadOnlyList<object?[]>? Rows { get; init; }
    public int RowCount { get; init; }
    public bool Truncated { get; init; }
    public long ExecutionMs { get; init; }

    /// <summary>Optional natural-language summary of the rows (only when requested and Answered).</summary>
    public string? Summary { get; init; }

    public string? Rationale { get; init; }
    public IReadOnlyList<string>? ReferencedTables { get; init; }

    /// <summary>Why the question was refused (validation errors or model decline). Null when Answered.</summary>
    public IReadOnlyList<string>? RefusalReasons { get; init; }

    public static AnalystResult Refused(
        string question, string? generatedSql, IReadOnlyList<string> reasons, string? rationale) => new()
    {
        Status = AnalystStatus.Refused,
        Question = question,
        GeneratedSql = generatedSql,
        RefusalReasons = reasons,
        Rationale = rationale
    };

    public static AnalystResult Answered(
        string question, string generatedSql, SqlValidationResult validation, QueryResult result,
        string? rationale, string? summary) => new()
    {
        Status = AnalystStatus.Answered,
        Question = question,
        GeneratedSql = generatedSql,
        ExecutedSql = validation.SafeSql,
        Columns = result.Columns,
        Rows = result.Rows,
        RowCount = result.RowCount,
        Truncated = result.Truncated,
        ExecutionMs = result.ExecutionMs,
        ReferencedTables = validation.ReferencedTables,
        Rationale = rationale,
        Summary = summary
    };
}
