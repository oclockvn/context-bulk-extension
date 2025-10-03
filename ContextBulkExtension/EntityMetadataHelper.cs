using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Collections.Concurrent;

namespace ContextBulkExtension;

/// <summary>
/// Helper class for extracting entity metadata from EF Core model.
/// </summary>
internal static class EntityMetadataHelper
{
    private static readonly ConcurrentDictionary<(Type EntityType, Type ContextType), CachedEntityMetadata> _cache = new();
    /// <summary>
    /// Extracts column metadata for bulk insert operations.
    /// </summary>
    /// <param name="context">The DbContext instance</param>
    /// <param name="includeIdentity">Whether to include identity columns. Default is false.</param>
    public static List<ColumnMetadata> GetColumnMetadata<T>(DbContext context, bool includeIdentity = false) where T : class
    {
        var cacheKey = (typeof(T), context.GetType());

        var cached = _cache.GetOrAdd(cacheKey, _ => BuildEntityMetadata<T>(context));

        return includeIdentity ? cached.ColumnsWithIdentity : cached.Columns;
    }

    /// <summary>
    /// Builds complete entity metadata including columns and table name.
    /// </summary>
    private static CachedEntityMetadata BuildEntityMetadata<T>(DbContext context) where T : class
    {
        var entityType = context.Model.FindEntityType(typeof(T));
        if (entityType == null)
        {
            throw new InvalidOperationException(
                $"Entity type '{typeof(T).Name}' is not part of the DbContext model. " +
                "Ensure the entity is configured in your DbContext.");
        }

        var allColumns = new List<ColumnMetadata>();
        var columnsWithoutIdentity = new List<ColumnMetadata>();

        foreach (var property in entityType.GetProperties())
        {
            // Skip shadow properties that are not foreign keys
            if (property.IsShadowProperty() && !property.IsForeignKey())
                continue;

            // Skip computed columns
            if (property.GetComputedColumnSql() != null)
                continue;

            // Skip properties with AfterSaveBehavior
            if (property.ValueGenerated == ValueGenerated.OnAddOrUpdate ||
                property.ValueGenerated == ValueGenerated.OnUpdate)
                continue;

            var clrProperty = property.PropertyInfo;
            if (clrProperty == null)
                continue;

            var columnName = property.GetColumnName();
            if (string.IsNullOrEmpty(columnName))
                continue;

            var clrType = property.ClrType;

            var columnMetadata = new ColumnMetadata
            {
                ColumnName = columnName,
                PropertyInfo = clrProperty,
                ClrType = Nullable.GetUnderlyingType(clrType) ?? clrType
            };

            allColumns.Add(columnMetadata);

            // Exclude identity columns from non-identity list
            bool isIdentity = property.ValueGenerated == ValueGenerated.OnAdd &&
                             (property.GetValueGeneratorFactory() != null ||
                              property.GetDefaultValueSql() != null);

            if (!isIdentity)
            {
                columnsWithoutIdentity.Add(columnMetadata);
            }
        }

        if (columnsWithoutIdentity.Count == 0)
        {
            throw new InvalidOperationException(
                $"No valid columns found for entity type '{typeof(T).Name}'. " +
                "Ensure the entity has mapped properties.");
        }

        // Build table name
        var tableName = entityType.GetTableName();
        var schema = entityType.GetSchema();

        if (string.IsNullOrEmpty(tableName))
        {
            throw new InvalidOperationException(
                $"Could not determine table name for entity type '{typeof(T).Name}'.");
        }

        var fullTableName = string.IsNullOrEmpty(schema)
            ? $"[{tableName}]"
            : $"[{schema}].[{tableName}]";

        return new CachedEntityMetadata
        {
            Columns = columnsWithoutIdentity,
            ColumnsWithIdentity = allColumns,
            TableName = fullTableName
        };
    }

    /// <summary>
    /// Gets the full table name including schema.
    /// </summary>
    public static string GetTableName<T>(DbContext context) where T : class
    {
        var cacheKey = (typeof(T), context.GetType());

        var cached = _cache.GetOrAdd(cacheKey, _ => BuildEntityMetadata<T>(context));

        return cached.TableName;
    }

    /// <summary>
    /// Checks if entity has identity column.
    /// </summary>
    public static bool HasIdentityColumn<T>(DbContext context) where T : class
    {
        var cacheKey = (typeof(T), context.GetType());

        var cached = _cache.GetOrAdd(cacheKey, _ => BuildEntityMetadata<T>(context));

        // If columns with identity differ from regular columns, there are identity columns
        return cached.ColumnsWithIdentity.Count > cached.Columns.Count;
    }

    /// <summary>
    /// Clears the metadata cache. Useful for testing or when model changes at runtime.
    /// </summary>
    public static void ClearCache()
    {
        _cache.Clear();
    }

}
