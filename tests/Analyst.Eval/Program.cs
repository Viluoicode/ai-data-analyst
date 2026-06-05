using System.Globalization;
using System.Text.Json;
using Analyst.Core;
using Analyst.Core.Configuration;
using Analyst.Core.Sql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// ---------------------------------------------------------------------------------------------
// Evaluation harness. Measures two things the project actually cares about:
//   1. Text-to-SQL accuracy  — pipeline output vs a reference query, by RESULT-SET equivalence.
//   2. Safety                — known-malicious SQL must be blocked; benign SQL must pass.
// Runs against the offline provider by default (no keys). Set ANALYST_PROVIDER=AzureOpenAI
// (+ ANALYST_AZURE_* env vars) to measure a real model on the same golden set.
// ---------------------------------------------------------------------------------------------

string Base(string rel) => Path.Combine(AppContext.BaseDirectory, rel);

var connectionString = Environment.GetEnvironmentVariable("ANALYST_CONNSTRING")
    ?? "Server=localhost,11433;Database=AnalystDB;User Id=analyst_ro;Password=Readonly#Analyst1;TrustServerCertificate=True;Encrypt=False";
var providerName = Environment.GetEnvironmentVariable("ANALYST_PROVIDER") ?? "Offline";
var provider = Enum.Parse<LlmProvider>(providerName, ignoreCase: true);

var options = new AnalystCoreOptions
{
    SchemaConfigPath = Base("config/schema.fnb.json"),
    ConnectionString = connectionString,
    Provider = provider
};
if (provider == LlmProvider.AzureOpenAI)
{
    options.Azure.Endpoint = Environment.GetEnvironmentVariable("ANALYST_AZURE_ENDPOINT") ?? "";
    options.Azure.DeploymentName = Environment.GetEnvironmentVariable("ANALYST_AZURE_DEPLOYMENT") ?? "";
    options.Azure.ApiKey = Environment.GetEnvironmentVariable("ANALYST_AZURE_KEY") ?? "";
}
else if (provider == LlmProvider.OpenAI)
{
    // Also covers OpenAI-compatible servers like local Ollama (set ANALYST_OPENAI_BASEURL).
    options.OpenAI.BaseUrl = Environment.GetEnvironmentVariable("ANALYST_OPENAI_BASEURL") ?? "";
    options.OpenAI.Model = Environment.GetEnvironmentVariable("ANALYST_OPENAI_MODEL") ?? options.OpenAI.Model;
    options.OpenAI.ApiKey = Environment.GetEnvironmentVariable("ANALYST_OPENAI_APIKEY") ?? "ollama";
}

var services = new ServiceCollection();
services.AddLogging(b => b.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning));
services.AddAnalystCore(options);
using var sp = services.BuildServiceProvider();

var analyst = sp.GetRequiredService<AnalystService>();
var validator = sp.GetRequiredService<SqlValidator>();
var executor = sp.GetRequiredService<ISqlExecutor>();

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine($"=== AI Data Analyst — Evaluation (provider: {provider}) ===\n");

// ----------------------------------------------------------------- 1. Text-to-SQL accuracy
Console.WriteLine("Text-to-SQL accuracy (result-set equivalence):");
var questions = LoadJsonl<EvalQuestion>(Base("eval/questions.jsonl"));

var passed = 0;
var byDifficulty = new Dictionary<string, (int pass, int total)>(StringComparer.OrdinalIgnoreCase);
var latencies = new List<long>();

foreach (var q in questions)
{
    var (ok, note, ms) = await EvaluateAsync(q);
    if (ms is { } m) latencies.Add(m);

    var prior = byDifficulty.GetValueOrDefault(q.Difficulty);
    byDifficulty[q.Difficulty] = (prior.pass + (ok ? 1 : 0), prior.total + 1);
    if (ok) passed++;

    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  [{q.Difficulty,-6}] {q.Lang.ToUpperInvariant(),-2}  {q.Question}{(note.Length > 0 ? $"   -> {note}" : "")}");
}

var accuracyPct = questions.Count == 0 ? 0 : 100.0 * passed / questions.Count;
Console.WriteLine($"\n  Accuracy: {passed}/{questions.Count} ({accuracyPct:0.#}%)");
Console.WriteLine("  By difficulty: " +
    string.Join(", ", byDifficulty.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value.pass}/{k.Value.total}")));

// ----------------------------------------------------------------- 2. Safety suite
Console.WriteLine("\nSafety suite (validator must block malicious SQL, allow benign SQL):");
var safety = LoadJsonl<SafetyCase>(Base("eval/safety.jsonl"));

int maliciousTotal = 0, maliciousBlocked = 0, benignTotal = 0, benignAllowed = 0, falseRefusals = 0;

foreach (var s in safety)
{
    var rejected = !validator.Validate(s.Sql).IsValid;
    if (s.Expect.Equals("reject", StringComparison.OrdinalIgnoreCase))
    {
        maliciousTotal++;
        if (rejected) maliciousBlocked++;
        Console.WriteLine($"  {(rejected ? "BLOCK " : "LEAK! ")} {s.Name}");
    }
    else
    {
        benignTotal++;
        if (rejected) { falseRefusals++; Console.WriteLine($"  REFUSED {s.Name}  (false refusal)"); }
        else { benignAllowed++; Console.WriteLine($"  ALLOW  {s.Name}"); }
    }
}

Console.WriteLine($"\n  Malicious blocked: {maliciousBlocked}/{maliciousTotal}" +
    $" ({Pct(maliciousBlocked, maliciousTotal):0.#}%)");
Console.WriteLine($"  Benign allowed:    {benignAllowed}/{benignTotal} (false refusals: {falseRefusals})");

// ----------------------------------------------------------------- scoreboard
var avgLatency = latencies.Count > 0 ? latencies.Average() : 0;
var safetyClean = maliciousBlocked == maliciousTotal && falseRefusals == 0;

Console.WriteLine("\n=== Scoreboard ===");
Console.WriteLine($"  Accuracy : {accuracyPct:0.#}%");
Console.WriteLine($"  Safety   : {(safetyClean ? "PASS" : "FAIL")} (block {Pct(maliciousBlocked, maliciousTotal):0.#}%, false-refusals {falseRefusals})");
Console.WriteLine($"  Latency  : avg {avgLatency:0} ms/query (execution)");

// Safety regressions fail the run (useful as a CI gate); accuracy is reported, not gated.
return safetyClean ? 0 : 1;

// ---------------------------------------------------------------------------------------------
async Task<(bool ok, string note, long? ms)> EvaluateAsync(EvalQuestion q)
{
    AnalystResult result;
    try { result = await analyst.AskAsync(q.Question); }
    catch (Exception ex) { return (false, $"pipeline error: {ex.Message}", null); }

    if (result.Status != AnalystStatus.Answered)
        return (false, $"refused: {string.Join("; ", result.RefusalReasons ?? [])}", null);

    QueryResult expected;
    try { expected = await executor.ExecuteAsync(q.Sql); }
    catch (Exception ex) { return (false, $"reference SQL error: {ex.Message}", result.ExecutionMs); }

    var ok = ResultsEquivalent(result.Columns!, result.Rows!, expected.Columns, expected.Rows);
    var note = ok ? "" : $"mismatch (got {result.RowCount} rows, expected {expected.RowCount})";
    return (ok, note, result.ExecutionMs);
}

static double Pct(int n, int d) => d == 0 ? 0 : 100.0 * n / d;

static bool ResultsEquivalent(
    IReadOnlyList<string> colsA, IReadOnlyList<object?[]> rowsA,
    IReadOnlyList<string> colsB, IReadOnlyList<object?[]> rowsB)
{
    if (colsA.Count != colsB.Count) return false;
    if (rowsA.Count != rowsB.Count) return false;

    var a = rowsA.Select(CanonRow).OrderBy(x => x, StringComparer.Ordinal);
    var b = rowsB.Select(CanonRow).OrderBy(x => x, StringComparer.Ordinal);
    return a.SequenceEqual(b);
}

static string CanonRow(object?[] row) => string.Join("␟", row.Select(CanonCell));

static string CanonCell(object? v) => v switch
{
    null => "(null)",
    decimal or double or float or int or long or short or byte or sbyte
        => Convert.ToDecimal(v, CultureInfo.InvariantCulture).ToString("0.##########", CultureInfo.InvariantCulture),
    DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
    _ => v.ToString() ?? ""
};

static List<T> LoadJsonl<T>(string path)
{
    var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    var items = new List<T>();
    foreach (var line in File.ReadAllLines(path))
    {
        if (string.IsNullOrWhiteSpace(line)) continue;
        items.Add(JsonSerializer.Deserialize<T>(line, opts)!);
    }
    return items;
}

internal sealed record EvalQuestion(string Id, string Lang, string Difficulty, string Question, string Sql);
internal sealed record SafetyCase(string Name, string Expect, string Sql);
