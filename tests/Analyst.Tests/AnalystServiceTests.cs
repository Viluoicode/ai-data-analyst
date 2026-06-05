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
        public Task<SqlGenerationResult> GenerateAsync(
            string question, GenerationContext? repair = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new SqlGenerationResult(sql, "stub", sql));
    }

    private static ISummarizer Summarizer => new TemplateSummarizer();

    /// <summary>Returns bad SQL first, then valid SQL once it receives repair feedback.</summary>
    private sealed class RepairingGenerator(string badSql, string goodSql) : ITextToSqlGenerator
    {
        public int Calls { get; private set; }

        public Task<SqlGenerationResult> GenerateAsync(
            string question, GenerationContext? repair = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            var sql = repair is null ? badSql : goodSql;
            return Task.FromResult(new SqlGenerationResult(sql, "stub", sql));
        }
    }

    /// <summary>Records whether execution happened and returns a canned result — no database needed.</summary>
    private sealed class FakeExecutor : ISqlExecutor
    {
        public int Calls { get; private set; }
        public string? LastSql { get; private set; }

        public Task<QueryResult> ExecuteAsync(string sql, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastSql = sql;
            return Task.FromResult(new QueryResult(["Revenue"], [new object?[] { 123m }], 1, false, 1));
        }
    }

    [Fact]
    public async Task Malicious_sql_is_refused_and_never_executed()
    {
        var executor = new FakeExecutor();
        var service = new AnalystService(
            new StubGenerator("DROP TABLE gold.DimStore;"),
            new SqlValidator(Schema.Value),
            executor,
            Summarizer,
            NullLogger<AnalystService>.Instance);

        var result = await service.AskAsync("please delete everything");

        Assert.Equal(AnalystStatus.Refused, result.Status);
        Assert.Equal(0, executor.Calls);          // the executor was never reached
        Assert.Null(result.ExecutedSql);
        Assert.Null(result.Rows);
        Assert.NotEmpty(result.RefusalReasons!);
    }

    [Fact]
    public async Task Empty_generation_is_refused()
    {
        var executor = new FakeExecutor();
        var service = new AnalystService(
            new StubGenerator(""),                // model declined
            new SqlValidator(Schema.Value),
            executor,
            Summarizer,
            NullLogger<AnalystService>.Instance);

        var result = await service.AskAsync("something unanswerable");

        Assert.Equal(AnalystStatus.Refused, result.Status);
        Assert.Equal(0, executor.Calls);
    }

    [Fact]
    public async Task Invalid_sql_is_repaired_then_executed()
    {
        var generator = new RepairingGenerator(
            badSql: "DROP TABLE gold.DimStore;",                                  // attempt 1: rejected
            goodSql: "SELECT SUM(f.LineTotal) AS Revenue FROM gold.FactOrderItem AS f;"); // attempt 2: valid
        var executor = new FakeExecutor();

        var service = new AnalystService(
            generator, new SqlValidator(Schema.Value), executor, Summarizer,
            NullLogger<AnalystService>.Instance);

        var result = await service.AskAsync("total revenue");

        Assert.Equal(AnalystStatus.Answered, result.Status);
        Assert.Equal(2, generator.Calls);         // initial + one repair
        Assert.Equal(1, executor.Calls);          // executed once, after repair
        Assert.NotNull(result.ExecutedSql);
    }

    [Fact]
    public async Task Summary_is_populated_when_requested()
    {
        var service = new AnalystService(
            new StubGenerator("SELECT SUM(f.LineTotal) AS Revenue FROM gold.FactOrderItem AS f;"),
            new SqlValidator(Schema.Value), new FakeExecutor(), Summarizer,
            NullLogger<AnalystService>.Instance);

        var withSummary = await service.AskAsync("total revenue", includeSummary: true);
        var without = await service.AskAsync("total revenue", includeSummary: false);

        Assert.False(string.IsNullOrWhiteSpace(withSummary.Summary));
        Assert.Null(without.Summary);
    }
}
