using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Analyst.Core;
using Analyst.Core.Configuration;
using ModelContextProtocol.Server;

namespace Analyst.Mcp;

/// <summary>
/// MCP tools over the read-only analytics pipeline. Services are injected from DI into each
/// tool method. Everything routes through <see cref="AnalystService"/>, so the same validation
/// and read-only guarantees apply to agent calls as to the REST API.
/// </summary>
[McpServerToolType]
public static class AnalystTools
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [McpServerTool(Name = "ask_data")]
    [Description("Answer a business question (English or Vietnamese) about the milk-tea sales data. " +
                 "Generates safe read-only SQL, validates it, runs it, and returns the SQL plus result rows.")]
    public static async Task<string> AskData(
        AnalystService analyst,
        [Description("The natural-language question, e.g. 'Top 5 products by revenue in 2025'.")] string question,
        [Description("Also return a short natural-language summary of the results.")] bool includeSummary = false,
        CancellationToken cancellationToken = default)
    {
        var result = await analyst.AskAsync(question, includeSummary, cancellationToken);
        return JsonSerializer.Serialize(result, Json);
    }

    [McpServerTool(Name = "list_schema")]
    [Description("List the tables and columns the analyst is allowed to query (the whitelist).")]
    public static string ListSchema(SchemaConfig schema)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Database: {schema.DatabaseName} (dialect: {schema.Dialect}, max rows: {schema.MaxRows})");
        sb.AppendLine(schema.Description);
        sb.AppendLine();
        foreach (var t in schema.Tables)
        {
            sb.AppendLine($"{t.FullName} — {t.Grain}");
            foreach (var c in t.Columns)
                sb.AppendLine($"  - {c.Name} ({c.Type}): {c.Description}");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
