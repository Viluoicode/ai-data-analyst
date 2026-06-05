using Analyst.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// MCP server over stdio. Exposes the same read-only analytics pipeline as tools an AI agent
// (Claude Desktop, etc.) can call. IMPORTANT: stdout is the JSON-RPC channel, so all logging
// must go to stderr — otherwise it corrupts the protocol stream.

// Pin the content root to the app's directory so appsettings.json / schema.fnb.json resolve
// no matter what working directory the MCP client launches us from.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

var coreOptions = new AnalystCoreOptions();
builder.Configuration.GetSection("Analyst").Bind(coreOptions);
coreOptions.ConnectionString =
    builder.Configuration.GetConnectionString("Analyst") ?? coreOptions.ConnectionString;
builder.Services.AddAnalystCore(coreOptions);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
