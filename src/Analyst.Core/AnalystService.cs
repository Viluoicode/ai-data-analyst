using System.Diagnostics;
using Analyst.Core.Llm;
using Analyst.Core.Sql;
using Microsoft.Extensions.Logging;

namespace Analyst.Core;

/// <summary>
/// The pipeline: question -> generate SQL -> VALIDATE -> (one repair retry) -> execute -> optional summary.
/// Validation is mandatory and fail-closed: nothing reaches the database unless it passed.
/// </summary>
public sealed class AnalystService
{
    private readonly ITextToSqlGenerator _generator;
    private readonly SqlValidator _validator;
    private readonly ISqlExecutor _executor;
    private readonly ISummarizer _summarizer;
    private readonly ILogger<AnalystService> _logger;

    public AnalystService(
        ITextToSqlGenerator generator, SqlValidator validator, ISqlExecutor executor,
        ISummarizer summarizer, ILogger<AnalystService> logger)
    {
        _generator = generator;
        _validator = validator;
        _executor = executor;
        _summarizer = summarizer;
        _logger = logger;
    }

    public async Task<AnalystResult> AskAsync(
        string question, bool includeSummary = false, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        var generated = await _generator.GenerateAsync(question, cancellationToken: cancellationToken);
        if (generated.IsEmpty)
        {
            _logger.LogInformation("Refused (model declined): {Rationale}", generated.Rationale);
            return AnalystResult.Refused(question, generated.Sql,
                [$"The model did not produce a query. {generated.Rationale}".Trim()], generated.Rationale);
        }

        var validation = _validator.Validate(generated.Sql);

        // One repair attempt: feed the validation errors back to the model and re-validate.
        if (!validation.IsValid)
        {
            _logger.LogWarning("Validation failed (attempt 1): {Reasons}", string.Join(" | ", validation.Errors));
            var repaired = await _generator.GenerateAsync(
                question, new GenerationContext(generated.Sql, validation.Errors), cancellationToken);

            if (!repaired.IsEmpty)
            {
                var repairedValidation = _validator.Validate(repaired.Sql);
                if (repairedValidation.IsValid)
                {
                    _logger.LogInformation("Repair attempt succeeded.");
                    generated = repaired;
                    validation = repairedValidation;
                }
            }
        }

        if (!validation.IsValid)
        {
            _logger.LogWarning("Refused after repair. SQL: {Sql}. Reasons: {Reasons}",
                generated.Sql, string.Join(" | ", validation.Errors));
            return AnalystResult.Refused(question, generated.Sql, validation.Errors, generated.Rationale);
        }

        var result = await _executor.ExecuteAsync(validation.SafeSql, cancellationToken);

        string? summary = null;
        if (includeSummary)
            summary = await _summarizer.SummarizeAsync(question, result, cancellationToken);

        stopwatch.Stop();
        _logger.LogInformation(
            "Answered in {TotalMs}ms (sql {ExecMs}ms), {Rows} rows, tables [{Tables}], summary={Summary}",
            stopwatch.ElapsedMilliseconds, result.ExecutionMs, result.RowCount,
            string.Join(", ", validation.ReferencedTables), includeSummary);

        return AnalystResult.Answered(question, generated.Sql, validation, result, generated.Rationale, summary);
    }
}
