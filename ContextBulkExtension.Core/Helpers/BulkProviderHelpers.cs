using System.Data.Common;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace ContextBulkExtension.Core.Helpers;

internal static class BulkProviderHelpers
{
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
