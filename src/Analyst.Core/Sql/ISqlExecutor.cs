namespace Analyst.Core.Sql;

/// <summary>Executes validated, read-only SQL and returns capped tabular results.</summary>
public interface ISqlExecutor
{
    Task<QueryResult> ExecuteAsync(string sql, CancellationToken cancellationToken = default);
}
