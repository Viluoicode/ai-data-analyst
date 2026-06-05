using System.Text.Json.Serialization;
using Analyst.Core;

var builder = WebApplication.CreateBuilder(args);

// Many cloud hosts (Render, Railway, Fly, …) inject the listen port via $PORT.
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(port))
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// Bind the Analyst section (provider, schema path, credentials) and register the Core pipeline.
var coreOptions = new AnalystCoreOptions();
builder.Configuration.GetSection("Analyst").Bind(coreOptions);
coreOptions.ConnectionString = builder.Configuration.GetConnectionString("Analyst") ?? coreOptions.ConnectionString;
builder.Services.AddAnalystCore(coreOptions);

// Serialize enums as strings ("Answered"/"Refused") instead of integers.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddOpenApi();

var app = builder.Build();

app.MapOpenApi(); // OpenAPI document at /openapi/v1.json

// Serve the thin demo page (wwwroot/index.html) at "/".
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ok", provider = coreOptions.Provider.ToString() }));

// POST /ask — natural-language question -> validated, read-only SQL -> result rows.
// Refused questions return 200 with status="Refused" and the reasons (the SQL never ran).
app.MapPost("/ask", async (AskRequest req, AnalystService analyst, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Question))
        return Results.BadRequest(new { error = "Question is required." });

    var result = await analyst.AskAsync(req.Question, req.IncludeSummary, ct);
    return Results.Ok(result);
});

app.Run();

public sealed record AskRequest(string Question, bool IncludeSummary = false);
