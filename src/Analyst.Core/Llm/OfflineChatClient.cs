using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Analyst.Core.Llm;

/// <summary>
/// A deterministic, offline <see cref="IChatClient"/> that returns canned SQL for known questions.
/// It exists so the full pipeline — validator, executor, and the evaluation harness — runs with
/// ZERO API keys. Swapping to a real provider (Azure OpenAI, etc.) is just a DI registration change.
/// Unknown questions get a benign fallback query, clearly marked in the rationale.
/// </summary>
public sealed partial class OfflineChatClient : IChatClient
{
    // Normalized question -> canned T-SQL. Doubles as ground truth for the eval golden set.
    private static readonly Dictionary<string, string> Canned = new(StringComparer.Ordinal)
    {
        ["what was total revenue in 2024?"] =
            "SELECT TOP 1000 SUM(f.LineTotal) AS TotalRevenue FROM gold.FactOrderItem AS f JOIN gold.DimDate AS d ON d.DateKey = f.DateKey WHERE d.[Year] = 2024;",

        ["top 5 best-selling products by quantity"] =
            "SELECT TOP 5 p.ProductName, SUM(f.Quantity) AS UnitsSold FROM gold.FactOrderItem AS f JOIN gold.DimProduct AS p ON p.ProductKey = f.ProductKey GROUP BY p.ProductName ORDER BY UnitsSold DESC;",

        ["doanh thu theo thành phố trong năm 2025"] =
            "SELECT TOP 1000 s.City, SUM(f.LineTotal) AS Revenue FROM gold.FactOrderItem AS f JOIN gold.DimStore AS s ON s.StoreKey = f.StoreKey JOIN gold.DimDate AS d ON d.DateKey = f.DateKey WHERE d.[Year] = 2025 GROUP BY s.City ORDER BY Revenue DESC;",

        ["how many orders were placed in 2025?"] =
            "SELECT TOP 1000 COUNT(DISTINCT f.OrderId) AS OrderCount FROM gold.FactOrderItem AS f JOIN gold.DimDate AS d ON d.DateKey = f.DateKey WHERE d.[Year] = 2025;",

        ["revenue by payment method"] =
            "SELECT TOP 1000 f.PaymentMethod, SUM(f.LineTotal) AS Revenue FROM gold.FactOrderItem AS f GROUP BY f.PaymentMethod ORDER BY Revenue DESC;",

        ["revenue by membership tier in 2024"] =
            "SELECT TOP 1000 c.MembershipTier, SUM(f.LineTotal) AS Revenue FROM gold.FactOrderItem AS f JOIN gold.DimCustomer AS c ON c.CustomerKey = f.CustomerKey JOIN gold.DimDate AS d ON d.DateKey = f.DateKey WHERE d.[Year] = 2024 GROUP BY c.MembershipTier ORDER BY Revenue DESC;",

        ["monthly revenue in 2025"] =
            "SELECT TOP 1000 d.MonthNum, d.MonthName, SUM(f.LineTotal) AS Revenue FROM gold.FactOrderItem AS f JOIN gold.DimDate AS d ON d.DateKey = f.DateKey WHERE d.[Year] = 2025 GROUP BY d.MonthNum, d.MonthName ORDER BY d.MonthNum;",

        ["top 3 stores by revenue"] =
            "SELECT TOP 3 s.StoreName, SUM(f.LineTotal) AS Revenue FROM gold.FactOrderItem AS f JOIN gold.DimStore AS s ON s.StoreKey = f.StoreKey GROUP BY s.StoreName ORDER BY Revenue DESC;",
    };

    private const string Fallback =
        "SELECT TOP 10 p.ProductName, p.Category, p.BasePrice FROM gold.DimProduct AS p ORDER BY p.BasePrice DESC;";

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var question = LastUserText(messages);
        var key = Normalize(question);

        var (sql, rationale) = Canned.TryGetValue(key, out var canned)
            ? (canned, "offline canned provider")
            : (Fallback, "offline fallback: no canned match for this question");

        var json = JsonSerializer.Serialize(new { sql, rationale });
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }

    private static string LastUserText(IEnumerable<ChatMessage> messages)
    {
        ChatMessage? last = null;
        foreach (var m in messages)
            if (m.Role == ChatRole.User)
                last = m;
        return last?.Text ?? string.Empty;
    }

    private static string Normalize(string s)
        => WhitespaceRegex().Replace(s.Trim().ToLowerInvariant(), " ");

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
