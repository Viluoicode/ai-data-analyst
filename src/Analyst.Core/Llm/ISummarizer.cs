using Analyst.Core.Sql;

namespace Analyst.Core.Llm;

/// <summary>Produces a short natural-language summary of a query result, in the question's language.</summary>
public interface ISummarizer
{
    Task<string> SummarizeAsync(string question, QueryResult result, CancellationToken cancellationToken = default);
}
