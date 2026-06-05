using System.Text;
using Analyst.Core.Sql;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Analyst.Core.Llm;

/// <summary>
/// Summarizes results with the configured chat model. Only a small sample of rows is sent
/// (never the whole result set), and the model is told to answer in the question's language.
/// </summary>
public sealed class LlmSummarizer : ISummarizer
{
    private const int MaxRowsForSummary = 20;

    private readonly IChatClient _chat;
    private readonly ILogger<LlmSummarizer> _logger;

    public LlmSummarizer(IChatClient chat, ILogger<LlmSummarizer> logger)
    {
        _chat = chat;
        _logger = logger;
    }

    public async Task<string> SummarizeAsync(string question, QueryResult result, CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System,
                "You are a data analyst. Write a concise 1-3 sentence summary of the query results for a " +
                "business user. Reply IN THE SAME LANGUAGE as the user's question (Vietnamese or English). " +
                "Cite the most important numbers. Do not invent any data that is not in the results."),
            new(ChatRole.User,
                $"Question: {question}\n\nResults ({result.RowCount} rows{(result.Truncated ? ", truncated" : "")}):\n{Render(result)}")
        };

        var response = await _chat.GetResponseAsync(messages, new ChatOptions { Temperature = 0.2f }, cancellationToken);
        _logger.LogInformation("Summarized {Rows} rows", result.RowCount);
        return (response.Text ?? string.Empty).Trim();
    }

    private static string Render(QueryResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(" | ", result.Columns));
        foreach (var row in result.Rows.Take(MaxRowsForSummary))
            sb.AppendLine(string.Join(" | ", row.Select(v => v?.ToString() ?? "NULL")));
        return sb.ToString();
    }
}
