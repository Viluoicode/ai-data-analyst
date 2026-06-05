using System.Text;
using System.Text.Json;
using Analyst.Core.Configuration;
using Microsoft.Extensions.AI;

namespace Analyst.Core.Llm;

/// <summary>
/// Builds the chat messages sent to the model. The system prompt is rendered ONCE from the
/// schema config, so the model is told about exactly the tables/columns the validator allows.
/// </summary>
public sealed class PromptBuilder
{
    private readonly SchemaConfig _schema;
    private readonly Lazy<string> _systemPrompt;

    public PromptBuilder(SchemaConfig schema)
    {
        _schema = schema;
        _systemPrompt = new Lazy<string>(BuildSystemPrompt);
    }

    public string SystemPrompt => _systemPrompt.Value;

    /// <summary>System prompt + few-shot examples + the user's question as the final user turn.</summary>
    /// <param name="repair">When set, appends the rejected SQL and validator errors so the model can fix it.</param>
    public IList<ChatMessage> BuildMessages(string question, GenerationContext? repair = null)
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, SystemPrompt) };

        foreach (var ex in _schema.FewShot)
        {
            messages.Add(new ChatMessage(ChatRole.User, ex.Question));
            messages.Add(new ChatMessage(ChatRole.Assistant,
                JsonSerializer.Serialize(new { sql = ex.Sql, rationale = "example" })));
        }

        messages.Add(new ChatMessage(ChatRole.User, question));

        if (repair is not null)
        {
            messages.Add(new ChatMessage(ChatRole.Assistant,
                JsonSerializer.Serialize(new { sql = repair.PreviousSql, rationale = "previous attempt" })));
            messages.Add(new ChatMessage(ChatRole.User,
                "That query was REJECTED by the SQL validator for these reasons:\n" +
                string.Join("\n", repair.Errors.Select(e => $"  - {e}")) +
                "\nFix the query so it obeys every hard rule (single SELECT, only allowed tables/columns, TOP limit). " +
                "Respond again with the JSON object only."));
        }

        return messages;
    }

    private string BuildSystemPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a senior data analyst. You translate a business question (Vietnamese or English) into ONE read-only Microsoft SQL Server (T-SQL) query.");
        sb.AppendLine();
        sb.AppendLine($"Database: {_schema.DatabaseName} (T-SQL / SQL Server).");
        sb.AppendLine(_schema.Description);
        sb.AppendLine();
        sb.AppendLine("You may query ONLY these tables and columns. Never use any table or column not listed here:");
        sb.AppendLine();

        foreach (var t in _schema.Tables)
        {
            sb.AppendLine($"{t.FullName} — {t.Grain}");
            foreach (var c in t.Columns)
                sb.AppendLine($"  - {c.Name} ({c.Type}): {c.Description}");
            sb.AppendLine();
        }

        if (_schema.Relationships.Count > 0)
        {
            sb.AppendLine("Relationships (join keys):");
            foreach (var r in _schema.Relationships)
                sb.AppendLine($"  {r.From} -> {r.To}");
            sb.AppendLine();
        }

        sb.AppendLine("HARD RULES:");
        sb.AppendLine($"  1. Output exactly ONE statement, and it MUST be a SELECT. No INSERT/UPDATE/DELETE/MERGE/DDL/EXEC, no semicolons chaining multiple statements.");
        sb.AppendLine($"  2. Always limit rows with TOP. Use TOP {_schema.MaxRows} unless the question asks for a smaller \"top N\".");
        sb.AppendLine("  3. Use ONLY the tables/columns listed above, and schema-qualify every table (e.g. gold.FactOrderItem). Do not invent names.");
        sb.AppendLine("  4. Quote reserved words in brackets, e.g. [Year].");
        sb.AppendLine("  5. Revenue = SUM(LineTotal). Number of orders = COUNT(DISTINCT OrderId). Filter by time via a join to gold.DimDate.");
        sb.AppendLine("  6. If the question cannot be answered from this schema, set \"sql\" to an empty string and explain why in \"rationale\".");
        sb.AppendLine();
        sb.AppendLine("Respond ONLY with a JSON object of the form {\"sql\": \"<the SELECT query>\", \"rationale\": \"<one short sentence>\"}. No markdown, no extra text.");

        return sb.ToString();
    }
}
