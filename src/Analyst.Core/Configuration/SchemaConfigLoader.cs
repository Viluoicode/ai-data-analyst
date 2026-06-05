using System.Text.Json;

namespace Analyst.Core.Configuration;

/// <summary>Loads and validates the schema-config JSON.</summary>
public static class SchemaConfigLoader
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static SchemaConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Schema config not found at '{path}'. Set Analyst:SchemaConfigPath or copy config/schema.fnb.json next to the app.", path);

        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<SchemaConfig>(json, Options)
            ?? throw new InvalidOperationException($"Schema config at '{path}' deserialized to null.");

        if (config.Tables.Count == 0)
            throw new InvalidOperationException($"Schema config at '{path}' contains no tables.");

        return config;
    }
}
