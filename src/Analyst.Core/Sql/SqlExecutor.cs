using System.Diagnostics;
using Analyst.Core.Configuration;
using Microsoft.Data.SqlClient;

namespace Analyst.Core.Sql;

/// <summary>
/// Executes already-validated SQL through the least-privilege read-only connection.
/// Applies a command timeout and a HARD reader-side row cap — the guaranteed backstop even if
/// the TOP rewrite was skipped (e.g. for a UNION). The connection string itself uses the
/// <c>analyst_ro</c> principal, which cannot write, so this layer cannot mutate data.
/// </summary>
public sealed class SqlExecutor : ISqlExecutor
{
    private readonly string _connectionString;
    private readonly int _maxRows;
    private readonly int _timeoutSeconds;

    public SqlExecutor(string connectionString, SchemaConfig schema)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("No connection string configured (ConnectionStrings:Analyst).");
        _connectionString = connectionString;
        _maxRows = schema.MaxRows;
        _timeoutSeconds = schema.QueryTimeoutSeconds;
    }

    public async Task<QueryResult> ExecuteAsync(string sql, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection) { CommandTimeout = _timeoutSeconds };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var columns = new string[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
            columns[i] = reader.GetName(i);

        var rows = new List<object?[]>();
        var truncated = false;

        while (await reader.ReadAsync(cancellationToken))
        {
            if (rows.Count >= _maxRows)
            {
                truncated = true;
                break;
            }

            var row = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var value = reader.GetValue(i);
                row[i] = value is DBNull ? null : value;
            }
            rows.Add(row);
        }

        sw.Stop();
        return new QueryResult(columns, rows, rows.Count, truncated, sw.ElapsedMilliseconds);
    }
}
