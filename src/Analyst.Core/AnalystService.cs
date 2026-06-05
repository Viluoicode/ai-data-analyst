using Analyst.Core.Llm;
using Analyst.Core.Sql;
using Microsoft.Extensions.Logging;

namespace Analyst.Core;

/// <summary>
/// The pipeline: question -> generate SQL -> VALIDATE -> execute -> result.
/// Validation is mandatory and fail-closed: nothing reaches the database unless it passed.
/// </summary>
public sealed class AnalystService
{
    private readonly ITextToSqlGenerator _generator;
    private readonly SqlValidator _validator;
    private readonly SqlExecutor _executor;
    private readonly ILogger<AnalystService> _logger;

    public AnalystService(
        ITextToSqlGenerator generator, SqlValidator validator, SqlExecutor executor, ILogger<AnalystService> logger)
    {
        _generator = generator;
        _validator = validator;
        _executor = executor;
        _logger = logger;
    }

    public async Task<AnalystResult> AskAsync(string question, CancellationToken cancellationToken = default)
    {
        var generated = await _generator.GenerateAsync(question, cancellationToken);

        if (generated.IsEmpty)
        {
            _logger.LogInformation("Model declined to answer: {Rationale}", generated.Rationale);
            return AnalystResult.Refused(question, generated.Sql,
                [$"The model did not produce a query. {generated.Rationale}".Trim()], generated.Rationale);
        }

        var validation = _validator.Validate(generated.Sql);
        if (!validation.IsValid)
        {
            _logger.LogWarning("Validation rejected SQL for question. Reasons: {Reasons}. SQL: {Sql}",
                string.Join(" | ", validation.Errors), generated.Sql);
            return AnalystResult.Refused(question, generated.Sql, validation.Errors, generated.Rationale);
        }

        var result = await _executor.ExecuteAsync(validation.SafeSql, cancellationToken);
        _logger.LogInformation("Answered question in {Ms}ms, {Rows} rows, tables [{Tables}]",
            result.ExecutionMs, result.RowCount, string.Join(", ", validation.ReferencedTables));

        return AnalystResult.Answered(question, generated.Sql, validation, result, generated.Rationale);
    }
}
