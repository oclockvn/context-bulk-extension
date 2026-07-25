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

    private static ColumnMetadata Col(string propName, string columnName, bool isIdentity = false, bool isPk = false)
    {
        var prop = typeof(Sample).GetProperty(propName, BindingFlags.Public | BindingFlags.Instance)!;
        return new ColumnMetadata
        {
            ColumnName = columnName,
            SqlType = "nvarchar",
            PropertyInfo = prop,
            ClrType = prop.PropertyType,
            ProviderClrType = prop.PropertyType,
            PropertyName = propName,
            CompiledGetter = _ => null,
            CompiledSetter = (_, _) => { },
            IsIdentity = isIdentity,
            IsPrimaryKey = isPk
        };
    }
}
