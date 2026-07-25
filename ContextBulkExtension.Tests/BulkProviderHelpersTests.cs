using System.Reflection;
using ContextBulkExtension.Core.Helpers;

namespace ContextBulkExtension.Tests;

public class BulkProviderHelpersTests
{
    private sealed class Sample
    {
        public int Id { get; set; }
        public string Email { get; set; } = "";
        public string Name { get; set; } = "";
        public int Points { get; set; }
    }

    [Fact]
    public void FilterUpdateColumns_ExcludesIdentityAndMatchKeys()
    {
        var id = Col(nameof(Sample.Id), "Id", isIdentity: true, isPk: true);
        var email = Col(nameof(Sample.Email), "Email", isPk: true);
        var name = Col(nameof(Sample.Name), "Name");
        var points = Col(nameof(Sample.Points), "Points");

        var result = BulkProviderHelpers.FilterUpdateColumns(
            [id, email, name, points],
            matchKeyColumns: [email],
            updateColumnNames: null);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, c => c.ColumnName == "Name");
        Assert.Contains(result, c => c.ColumnName == "Points");
        Assert.DoesNotContain(result, c => c.IsIdentity);
        Assert.DoesNotContain(result, c => c.ColumnName == "Email");
    }

    [Fact]
    public void FilterUpdateColumns_RespectsUpdateColumnNamesFilter()
    {
        var id = Col(nameof(Sample.Id), "Id", isIdentity: true, isPk: true);
        var email = Col(nameof(Sample.Email), "Email");
        var name = Col(nameof(Sample.Name), "Name");
        var points = Col(nameof(Sample.Points), "Points");

        var result = BulkProviderHelpers.FilterUpdateColumns(
            [id, email, name, points],
            matchKeyColumns: [id],
            updateColumnNames: [nameof(Sample.Name)]);

        Assert.Single(result);
        Assert.Equal("Name", result[0].ColumnName);
    }

    [Fact]
    public void FilterUpdateColumns_EmptyUpdateColumnNames_KeepsAllEligible()
    {
        var id = Col(nameof(Sample.Id), "Id", isIdentity: true, isPk: true);
        var name = Col(nameof(Sample.Name), "Name");
        var points = Col(nameof(Sample.Points), "Points");

        var result = BulkProviderHelpers.FilterUpdateColumns(
            [id, name, points],
            matchKeyColumns: [id],
            updateColumnNames: []);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void EnsurePostgresIdentityOutputSupported_SingleInt_Ok()
    {
        var id = Col(nameof(Sample.Id), "Id", isIdentity: true, isPk: true);

        BulkProviderHelpers.EnsurePostgresIdentityOutputSupported([id]);
    }

    [Fact]
    public void EnsurePostgresIdentityOutputSupported_SingleLong_Ok()
    {
        var id = Col(nameof(Sample.Id), "Id", isIdentity: true, isPk: true, clrType: typeof(long));

        BulkProviderHelpers.EnsurePostgresIdentityOutputSupported([id]);
    }

    [Fact]
    public void EnsurePostgresIdentityOutputSupported_ValueConvertedLong_Ok()
    {
        var id = Col(
            nameof(Sample.Id),
            "Id",
            isIdentity: true,
            isPk: true,
            clrType: typeof(Guid),
            providerClrType: typeof(long));

        BulkProviderHelpers.EnsurePostgresIdentityOutputSupported([id]);
    }

    [Fact]
    public void EnsurePostgresIdentityOutputSupported_Guid_Throws()
    {
        var id = Col(nameof(Sample.Id), "Id", isIdentity: true, isPk: true, clrType: typeof(Guid));

        var ex = Assert.Throws<NotSupportedException>(
            () => BulkProviderHelpers.EnsurePostgresIdentityOutputSupported([id]));

        Assert.Contains("serial/bigserial", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsurePostgresIdentityOutputSupported_Multiple_Throws()
    {
        var id = Col(nameof(Sample.Id), "Id", isIdentity: true, isPk: true);
        var points = Col(nameof(Sample.Points), "Points", isIdentity: true);

        var ex = Assert.Throws<NotSupportedException>(
            () => BulkProviderHelpers.EnsurePostgresIdentityOutputSupported([id, points]));

        Assert.Contains("single", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsurePostgresIdentityOutputSupported_Empty_Throws()
    {
        Assert.Throws<NotSupportedException>(
            () => BulkProviderHelpers.EnsurePostgresIdentityOutputSupported([]));
    }

    [Theory]
    [InlineData("target.[AccountId] = @p0", "target.\"AccountId\" = @p0")]
    [InlineData("(target.[A] = @p0 AND target.[B] = @p1)", "(target.\"A\" = @p0 AND target.\"B\" = @p1)")]
    [InlineData("target.[Col]]] = @p0", "target.\"Col]\" = @p0")]
    [InlineData("target.[Weird\"Name] = @p0", "target.\"Weird\"\"Name\" = @p0")]
    public void ToPostgresTargetQualified_RewritesBrackets(string input, string expected)
    {
        Assert.Equal(expected, BulkProviderHelpers.ToPostgresTargetQualified(input));
    }

    private static ColumnMetadata Col(
        string propName,
        string columnName,
        bool isIdentity = false,
        bool isPk = false,
        Type? clrType = null,
        Type? providerClrType = null)
    {
        var prop = typeof(Sample).GetProperty(propName, BindingFlags.Public | BindingFlags.Instance)!;
        return new ColumnMetadata
        {
            ColumnName = columnName,
            SqlType = "nvarchar",
            PropertyInfo = prop,
            ClrType = clrType ?? prop.PropertyType,
            ProviderClrType = providerClrType ?? clrType ?? prop.PropertyType,
            PropertyName = propName,
            CompiledGetter = _ => null,
            CompiledSetter = (_, _) => { },
            IsIdentity = isIdentity,
            IsPrimaryKey = isPk
        };
    }
}
