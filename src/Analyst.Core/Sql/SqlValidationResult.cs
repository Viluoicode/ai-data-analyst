namespace Analyst.Core.Sql;

/// <summary>Outcome of validating a generated query.</summary>
/// <param name="IsValid">True only if every safety rule passed.</param>
/// <param name="SafeSql">The validated SQL, rewritten with an enforced row cap. Empty when invalid.</param>
/// <param name="Errors">Human-readable reasons the query was rejected.</param>
/// <param name="ReferencedTables">Whitelisted tables the query touches (for logging/auditing).</param>
public sealed record SqlValidationResult(
    bool IsValid,
    string SafeSql,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> ReferencedTables)
{
    public static SqlValidationResult Invalid(IReadOnlyList<string> errors)
        => new(false, "", errors, []);

    public static SqlValidationResult Invalid(string error)
        => new(false, "", [error], []);

    public static SqlValidationResult Valid(string safeSql, IReadOnlyList<string> referencedTables)
        => new(true, safeSql, [], referencedTables);
}
