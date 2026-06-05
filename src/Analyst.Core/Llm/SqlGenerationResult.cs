namespace Analyst.Core.Llm;

/// <summary>Result of turning a natural-language question into a SQL candidate.</summary>
/// <param name="Sql">The generated SQL (not yet validated or executed). Empty if the model declined.</param>
/// <param name="Rationale">Optional short explanation the model gave.</param>
/// <param name="RawResponse">The raw model response, kept for logging/debugging.</param>
public sealed record SqlGenerationResult(string Sql, string? Rationale, string RawResponse)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Sql);
}

public interface ITextToSqlGenerator
{
    Task<SqlGenerationResult> GenerateAsync(string question, CancellationToken cancellationToken = default);
}
