using Analyst.Core.Configuration;
using Analyst.Core.Sql;

namespace Analyst.Tests;

/// <summary>
/// One assertion per safety rule. This suite is the contract for "the model is untrusted":
/// anything that is not a single, whitelisted, read-only SELECT must be rejected before execution.
/// </summary>
public class SqlValidatorTests
{
    private static readonly Lazy<SchemaConfig> Schema = new(() =>
        SchemaConfigLoader.Load(Path.Combine(AppContext.BaseDirectory, "config", "schema.fnb.json")));

    private static SqlValidationResult Validate(string sql) => new SqlValidator(Schema.Value).Validate(sql);

    // ---------------------------------------------------------------- valid queries pass

    [Fact]
    public void Simple_aggregate_passes()
    {
        var r = Validate("SELECT SUM(f.LineTotal) AS Revenue FROM gold.FactOrderItem AS f;");
        Assert.True(r.IsValid, string.Join(" | ", r.Errors));
        Assert.Contains("gold.FactOrderItem", r.ReferencedTables);
    }

    [Fact]
    public void Join_with_aliases_passes()
    {
        var r = Validate(
            "SELECT s.City, SUM(f.LineTotal) AS Revenue " +
            "FROM gold.FactOrderItem AS f JOIN gold.DimStore AS s ON s.StoreKey = f.StoreKey " +
            "GROUP BY s.City ORDER BY Revenue DESC;");
        Assert.True(r.IsValid, string.Join(" | ", r.Errors));
    }

    [Fact]
    public void Order_by_select_alias_passes()
    {
        // "Revenue" is an output alias, not a base column — must not be flagged.
        var r = Validate(
            "SELECT p.ProductName, SUM(f.Quantity) AS UnitsSold " +
            "FROM gold.FactOrderItem AS f JOIN gold.DimProduct AS p ON p.ProductKey = f.ProductKey " +
            "GROUP BY p.ProductName ORDER BY UnitsSold DESC;");
        Assert.True(r.IsValid, string.Join(" | ", r.Errors));
    }

    [Fact]
    public void Bracketed_reserved_word_column_passes()
    {
        var r = Validate(
            "SELECT d.[Year], SUM(f.LineTotal) AS Revenue " +
            "FROM gold.FactOrderItem AS f JOIN gold.DimDate AS d ON d.DateKey = f.DateKey " +
            "GROUP BY d.[Year];");
        Assert.True(r.IsValid, string.Join(" | ", r.Errors));
    }

    // ---------------------------------------------------------------- non-SELECT statements rejected

    [Theory]
    [InlineData("INSERT INTO gold.DimStore (StoreKey) VALUES (999);")]
    [InlineData("UPDATE gold.DimStore SET City = 'x' WHERE StoreKey = 1;")]
    [InlineData("DELETE FROM gold.DimStore WHERE StoreKey = 1;")]
    [InlineData("DROP TABLE gold.DimStore;")]
    [InlineData("TRUNCATE TABLE gold.FactOrderItem;")]
    [InlineData("EXEC sp_who;")]
    [InlineData("CREATE TABLE gold.Hack (id int);")]
    public void Non_select_statements_are_rejected(string sql)
    {
        Assert.False(Validate(sql).IsValid);
    }

    // ---------------------------------------------------------------- statement stacking rejected

    [Fact]
    public void Multiple_statements_are_rejected()
    {
        Assert.False(Validate("SELECT 1; SELECT 2;").IsValid);
    }

    [Fact]
    public void Select_then_drop_is_rejected()
    {
        var r = Validate("SELECT * FROM gold.DimProduct; DROP TABLE gold.DimProduct;");
        Assert.False(r.IsValid);
    }

    // ---------------------------------------------------------------- hallucinated names rejected

    [Fact]
    public void Unknown_table_is_rejected()
    {
        var r = Validate("SELECT * FROM gold.CustomerSecrets;");
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("CustomerSecrets"));
    }

    [Fact]
    public void Unknown_column_is_rejected()
    {
        var r = Validate("SELECT f.CreditCardNumber FROM gold.FactOrderItem AS f;");
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("CreditCardNumber"));
    }

    [Fact]
    public void Unqualified_unknown_column_is_rejected()
    {
        var r = Validate("SELECT Ssn FROM gold.DimCustomer;");
        Assert.False(r.IsValid);
    }

    // ---------------------------------------------------------------- dangerous constructs rejected

    [Fact]
    public void Select_into_is_rejected()
    {
        var r = Validate("SELECT * INTO gold.Hack FROM gold.DimProduct;");
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("INTO"));
    }

    [Fact]
    public void Openrowset_is_rejected()
    {
        var r = Validate(
            "SELECT a.* FROM OPENROWSET('SQLNCLI', 'Server=x;Trusted_Connection=yes;', " +
            "'SELECT 1') AS a;");
        Assert.False(r.IsValid);
    }

    [Fact]
    public void Cross_database_reference_is_rejected()
    {
        var r = Validate("SELECT * FROM master.dbo.sysobjects;");
        Assert.False(r.IsValid);
    }

    // ---------------------------------------------------------------- comments cannot smuggle a 2nd statement

    [Fact]
    public void Trailing_comment_does_not_bypass_single_statement_rule()
    {
        // The "; DROP" is commented out, so this is a single harmless SELECT — and stays valid.
        var r = Validate("SELECT p.ProductName FROM gold.DimProduct AS p -- ; DROP TABLE gold.DimProduct\n;");
        Assert.True(r.IsValid, string.Join(" | ", r.Errors));
    }

    // ---------------------------------------------------------------- row cap enforcement

    [Fact]
    public void Missing_top_gets_row_cap_injected()
    {
        var r = Validate("SELECT f.OrderId FROM gold.FactOrderItem AS f;");
        Assert.True(r.IsValid, string.Join(" | ", r.Errors));
        Assert.Contains("TOP", r.SafeSql.ToUpperInvariant());
        Assert.Contains("1000", r.SafeSql);
    }

    [Fact]
    public void Oversized_top_is_clamped()
    {
        var r = Validate("SELECT TOP 100000 f.OrderId FROM gold.FactOrderItem AS f;");
        Assert.True(r.IsValid, string.Join(" | ", r.Errors));
        Assert.DoesNotContain("100000", r.SafeSql);
        Assert.Contains("1000", r.SafeSql);
    }

    [Fact]
    public void Small_top_is_preserved()
    {
        var r = Validate("SELECT TOP 5 p.ProductName FROM gold.DimProduct AS p;");
        Assert.True(r.IsValid, string.Join(" | ", r.Errors));
        Assert.Contains("TOP", r.SafeSql.ToUpperInvariant());
        Assert.Contains("5", r.SafeSql);
    }
}
