using System.Data.Common;
using System.Linq.Expressions;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace ContextBulkExtension.Core.Helpers;

internal static class BulkProviderHelpers
{
    private static readonly Regex SqlServerBracketIdentifier = new(
        @"\[((?:\]\]|[^\]])*)\]",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// PostgreSQL IdentityOutput pairs RETURNING rows to staging via ROW_NUMBER ordered by
    /// the identity column vs __RowIndex. That requires a single monotonic int/long identity
    /// (serial/bigserial / GENERATED … AS IDENTITY). Guid/random/composite identities are unsupported.
    /// </summary>
    public static void EnsurePostgresIdentityOutputSupported(IReadOnlyList<ColumnMetadata> identityColumns)
    {
        if (identityColumns.Count != 1)
        {
            throw new NotSupportedException(
                "PostgreSQL IdentityOutput requires a single serial/bigserial (int/long) identity column.");
        }

        var clr = Nullable.GetUnderlyingType(identityColumns[0].ProviderClrType)
            ?? identityColumns[0].ProviderClrType;
        if (clr != typeof(int) && clr != typeof(long))
        {
            throw new NotSupportedException(
                $"PostgreSQL IdentityOutput requires a single serial/bigserial identity column (int/long). Got '{clr.Name}'.");
        }
    }

    /// <summary>
    /// Rewrites ExpressionHelper SQL Server bracket identifiers to Postgres double-quoted
    /// identifiers, unescaping ]] → ] and escaping " → "".
    /// </summary>
    public static string ToPostgresTargetQualified(string sqlServerStyleWhere)
    {
        ArgumentNullException.ThrowIfNull(sqlServerStyleWhere);
        return SqlServerBracketIdentifier.Replace(sqlServerStyleWhere, static match =>
        {
            var raw = match.Groups[1].Value.Replace("]]", "]", StringComparison.Ordinal);
            var escaped = raw.Replace("\"", "\"\"", StringComparison.Ordinal);
            return $"\"{escaped}\"";
        });
    }

    public static List<ColumnMetadata> FilterUpdateColumns(
        IReadOnlyList<ColumnMetadata> columns,
        IReadOnlyList<ColumnMetadata> matchKeyColumns,
        List<string>? updateColumnNames)
    {
        var matchKeyColumnNames = new HashSet<string>(
            matchKeyColumns.Select(pk => pk.ColumnName),
            StringComparer.OrdinalIgnoreCase);

        var updateColumns = columns
            .Where(c => !c.IsIdentity && !matchKeyColumnNames.Contains(c.ColumnName))
            .ToList();

        if (updateColumnNames?.Count > 0)
        {
            var updateNamesSet = new HashSet<string>(updateColumnNames, StringComparer.OrdinalIgnoreCase);
            updateColumns = [.. updateColumns.Where(c => updateNamesSet.Contains(c.PropertyInfo.Name))];
        }

        return updateColumns;
    }

    public static IReadOnlyList<ColumnMetadata> ResolveMatchColumns<T>(
        DbContext context,
        Expression<Func<T, object>>? matchOn) where T : class
    {
        if (matchOn != null)
        {
            var propertyNames = ExpressionHelper.ExtractPropertyNamesFromExpression(matchOn);
            var allColumns = EntityMetadataHelper.GetColumnMetadata<T>(context, includeIdentity: true);
            var propertyNamesSet = new HashSet<string>(propertyNames, StringComparer.OrdinalIgnoreCase);
            var matchColumns = allColumns.Where(c => propertyNamesSet.Contains(c.PropertyInfo.Name)).ToList();

            if (matchColumns.Count != propertyNames.Count)
            {
                var foundNames = new HashSet<string>(matchColumns.Select(c => c.PropertyInfo.Name), StringComparer.OrdinalIgnoreCase);
                var missing = propertyNames.Where(p => !foundNames.Contains(p));
                throw new InvalidOperationException($"Properties not found in entity metadata: {string.Join(", ", missing)}.");
            }

            return matchColumns;
        }

        var pkColumns = EntityMetadataHelper.GetPrimaryKeyColumns<T>(context);
        if (pkColumns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Entity type '{typeof(T).Name}' has no primary key defined. Either define a primary key or use matchOn parameter to specify custom match columns.");
        }

        return pkColumns;
    }

    public static void ApplyIdentityValues<T>(
        DbDataReader reader,
        IList<T> entities,
        IReadOnlyList<ColumnMetadata> identityColumns,
        int rowIndexOrdinal = 0) where T : class
    {
        var rowIndex = reader.GetInt32(rowIndexOrdinal);
        var entity = entities[rowIndex];

        for (int i = 0; i < identityColumns.Count; i++)
        {
            var identityColumn = identityColumns[i];
            var identityValue = reader.GetValue(rowIndexOrdinal + 1 + i);

            if (identityValue != null && identityValue != DBNull.Value && identityColumn.ValueConverter != null)
                identityValue = identityColumn.ValueConverter.ConvertFromProvider.Invoke(identityValue);

            identityColumn.CompiledSetter(entity, identityValue);
        }
    }
}
