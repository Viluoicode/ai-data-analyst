using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Analyst.Core.Llm;

/// <summary>
/// Provider-agnostic SQL generator. It talks to an <see cref="IChatClient"/>, so the actual
/// model (Azure OpenAI, OpenAI, Ollama, or the offline canned client) is just a DI choice.
/// Temperature 0 + JSON response format keep it deterministic and easy to parse.
/// </summary>
public sealed class TextToSqlGenerator : ITextToSqlGenerator
{
    private readonly IChatClient _chat;
    private readonly PromptBuilder _prompt;
    private readonly ILogger<TextToSqlGenerator> _logger;

    public TextToSqlGenerator(IChatClient chat, PromptBuilder prompt, ILogger<TextToSqlGenerator> logger)
    {
        _chat = chat;
        _prompt = prompt;
        _logger = logger;
    }

    public async Task<SqlGenerationResult> GenerateAsync(
        string question, GenerationContext? repair = null, CancellationToken cancellationToken = default)
    {
        var messages = _prompt.BuildMessages(question, repair);
        var options = new ChatOptions
        {
            Temperature = 0f,
            ResponseFormat = ChatResponseFormat.Json
        };

        var response = await _chat.GetResponseAsync(messages, options, cancellationToken);
        var raw = response.Text ?? string.Empty;

        var (sql, rationale) = Parse(raw);
        _logger.LogInformation("Generated SQL for question {QuestionLength} chars -> {SqlLength} chars sql",
            question.Length, sql.Length);

        return new SqlGenerationResult(sql, rationale, raw);
    }

    /// <summary>Parse the model's JSON {sql, rationale}; tolerate a bare SQL string or fenced code.</summary>
    internal static (string Sql, string? Rationale) Parse(string raw)
    {
        var text = raw.Trim();
        if (text.Length == 0)
            return (string.Empty, null);

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                var sql = doc.RootElement.TryGetProperty("sql", out var s) ? s.GetString() ?? "" : "";
                var rationale = doc.RootElement.TryGetProperty("rationale", out var r) ? r.GetString() : null;
                return (sql.Trim(), rationale);
            }
        }
        catch (JsonException)
        {
            // Not JSON — fall through and treat as raw SQL.
        }

        return (StripCodeFences(text), null);
    }

    private static string StripCodeFences(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal))
            return text;

        var firstNewline = text.IndexOf('\n');
        if (firstNewline < 0)
            return text;

        var body = text[(firstNewline + 1)..];
        var closing = body.LastIndexOf("```", StringComparison.Ordinal);
        if (closing >= 0)
            body = body[..closing];

        return body.Trim();
    }
}
