namespace Analyst.Core;

public enum LlmProvider
{
    /// <summary>Deterministic canned SQL. No API keys required. Default.</summary>
    Offline,
    AzureOpenAI,
    OpenAI
}

public sealed class AzureOpenAIOptions
{
    public string Endpoint { get; set; } = "";
    public string DeploymentName { get; set; } = "";
    public string ApiKey { get; set; } = "";
}

public sealed class OpenAIOptions
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-4o-mini";

    /// <summary>OpenAI-compatible base URL. Empty = api.openai.com. For Ollama: http://localhost:11434/v1</summary>
    public string BaseUrl { get; set; } = "";
}

public sealed class AnalystCoreOptions
{
    /// <summary>Path to schema.fnb.json. Relative paths resolve against the app base directory.</summary>
    public string SchemaConfigPath { get; set; } = "config/schema.fnb.json";

    /// <summary>Connection string for the read-only query principal (analyst_ro).</summary>
    public string ConnectionString { get; set; } = "";

    public LlmProvider Provider { get; set; } = LlmProvider.Offline;

    public AzureOpenAIOptions Azure { get; set; } = new();
    public OpenAIOptions OpenAI { get; set; } = new();

    public string ResolveSchemaPath()
        => Path.IsPathRooted(SchemaConfigPath)
            ? SchemaConfigPath
            : Path.Combine(AppContext.BaseDirectory, SchemaConfigPath);
}
