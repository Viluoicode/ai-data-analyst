using Analyst.Core;
using Analyst.Core.Configuration;
using Analyst.Core.Llm;
using Analyst.Core.Sql;
using Microsoft.Extensions.Logging.Abstractions;

namespace Analyst.Tests;

/// <summary>Proves the pipeline is fail-closed: invalid SQL is refused and never reaches the database.</summary>
public class AnalystServiceTests
{
    private static readonly Lazy<SchemaConfig> Schema = new(() =>
        SchemaConfigLoader.Load(Path.Combine(AppContext.BaseDirectory, "config", "schema.fnb.json")));

    private sealed class StubGenerator(string sql) : ITextToSqlGenerator
    {
        public Task<SqlGenerationResult> GenerateAsync(string question, CancellationToken cancellationToken = default)
            => Task.FromResult(new SqlGenerationResult(sql, "stub", sql));
    }

    [Fact]
    public async Task Malicious_sql_is_refused_and_never_executed()
    {
        // Executor points at an unreachable server; if the pipeline tried to run anything, this would throw.
        var executor = new SqlExecutor(
            "Server=unreachable.invalid,1;Database=x;User Id=u;Password=p;TrustServerCertificate=True;Connect Timeout=1",
            Schema.Value);

        var service = new AnalystService(
            new StubGenerator("DROP TABLE gold.DimStore;"),
            new SqlValidator(Schema.Value),
            executor,
            NullLogger<AnalystService>.Instance);

        var result = await service.AskAsync("please delete everything");

        Assert.Equal(AnalystStatus.Refused, result.Status);
        Assert.Null(result.ExecutedSql);          // nothing was executed
        Assert.Null(result.Rows);
        Assert.NotNull(result.RefusalReasons);
        Assert.NotEmpty(result.RefusalReasons!);
    }

    [Fact]
    public async Task Empty_generation_is_refused()
    {
        var service = new AnalystService(
            new StubGenerator(""),                // model declined
            new SqlValidator(Schema.Value),
            new SqlExecutor("Server=unreachable.invalid,1;Database=x;User Id=u;Password=p;Connect Timeout=1", Schema.Value),
            NullLogger<AnalystService>.Instance);

        var result = await service.AskAsync("something unanswerable");

        Assert.Equal(AnalystStatus.Refused, result.Status);
        Assert.Null(result.ExecutedSql);
    }
}
