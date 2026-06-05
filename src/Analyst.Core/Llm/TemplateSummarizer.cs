using Analyst.Core.Sql;

namespace Analyst.Core.Llm;

/// <summary>
/// Deterministic, offline summary used when no real LLM is configured. Picks Vietnamese or English
/// from the question text so the offline pipeline still returns a language-matched summary.
/// </summary>
public sealed class TemplateSummarizer : ISummarizer
{
    private const string VietnameseMarkers =
        "ăâđêôơưĂÂĐÊÔƠƯáàảãạấầẩẫậắằẳẵặéèẻẽẹếềểễệíìỉĩịóòỏõọốồổỗộớờởỡợúùủũụứừửữựýỳỷỹỵ";

    public Task<string> SummarizeAsync(string question, QueryResult result, CancellationToken cancellationToken = default)
    {
        var vi = IsVietnamese(question);
        string summary;

        if (result.RowCount == 0)
        {
            summary = vi ? "Truy vấn không trả về dòng nào." : "The query returned no rows.";
        }
        else if (result is { RowCount: 1, Columns.Count: 1 })
        {
            var value = result.Rows[0][0]?.ToString() ?? "NULL";
            summary = vi ? $"Kết quả: {result.Columns[0]} = {value}." : $"Result: {result.Columns[0]} = {value}.";
        }
        else
        {
            var cols = string.Join(", ", result.Columns);
            var firstRow = string.Join(", ",
                result.Columns.Select((c, i) => $"{c}={result.Rows[0][i]?.ToString() ?? "NULL"}"));
            summary = vi
                ? $"Truy vấn trả về {result.RowCount} dòng (cột: {cols}). Dòng đầu tiên: {firstRow}."
                : $"The query returned {result.RowCount} rows (columns: {cols}). First row: {firstRow}.";
        }

        if (result.Truncated)
            summary += vi ? " (đã giới hạn số dòng)" : " (row-capped)";

        return Task.FromResult(summary);
    }

    private static bool IsVietnamese(string text) => text.Any(c => VietnameseMarkers.Contains(c));
}
