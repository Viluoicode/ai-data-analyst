using Analyst.Core.Configuration;
using Analyst.Core.Llm;
using Analyst.Core.Sql;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Analyst.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the schema config, prompt builder, SQL generator, and an <see cref="IChatClient"/>
    /// chosen by <see cref="AnalystCoreOptions.Provider"/>. Default provider is Offline (no keys).
    /// </summary>
    public static IServiceCollection AddAnalystCore(this IServiceCollection services, AnalystCoreOptions options)
    {
        var schema = SchemaConfigLoader.Load(options.ResolveSchemaPath());

        services.AddSingleton(schema);
        services.AddSingleton<PromptBuilder>();
        services.AddSingleton<ITextToSqlGenerator, TextToSqlGenerator>();
        services.AddSingleton(BuildChatClient(options));

        // Offline mode summarizes deterministically; a real provider uses the chat model.
        if (options.Provider == LlmProvider.Offline)
            services.AddSingleton<ISummarizer, TemplateSummarizer>();
        else
            services.AddSingleton<ISummarizer, LlmSummarizer>();

        services.AddSingleton<SqlValidator>();
        services.AddSingleton<ISqlExecutor>(sp =>
            new SqlExecutor(options.ConnectionString, sp.GetRequiredService<SchemaConfig>()));
        services.AddSingleton<AnalystService>();

        return services;
    }

    private static IChatClient BuildChatClient(AnalystCoreOptions options) => options.Provider switch
    {
        LlmProvider.Offline => new OfflineChatClient(),

        LlmProvider.AzureOpenAI => CreateAzure(options.Azure),

        LlmProvider.OpenAI => new OpenAI.OpenAIClient(Require(options.OpenAI.ApiKey, "OpenAI:ApiKey"))
            .GetChatClient(options.OpenAI.Model)
            .AsIChatClient(),

        _ => throw new ArgumentOutOfRangeException(nameof(options), options.Provider, "Unknown LLM provider.")
    };

    private static IChatClient CreateAzure(AzureOpenAIOptions azure)
    {
        var endpoint = Require(azure.Endpoint, "Azure:Endpoint");
        var deployment = Require(azure.DeploymentName, "Azure:DeploymentName");
        var apiKey = Require(azure.ApiKey, "Azure:ApiKey");

        return new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey))
            .GetChatClient(deployment)
            .AsIChatClient();
    }

    private static string Require(string value, string name)
        => string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Missing configuration '{name}' for the selected LLM provider.")
            : value;
}
