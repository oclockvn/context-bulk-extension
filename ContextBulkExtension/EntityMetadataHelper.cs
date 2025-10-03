using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ContextBulkExtension;

/// <summary>
/// Helper class for extracting entity metadata from EF Core model.
/// </summary>
internal static class EntityMetadataHelper
{
    /// <summary>
    /// Extracts column metadata for bulk insert operations.
    /// </summary>
    /// <param name="context">The DbContext instance</param>
    /// <param name="includeIdentity">Whether to include identity columns. Default is false.</param>
    public static List<ColumnMetadata> GetColumnMetadata<T>(DbContext context, bool includeIdentity = false) where T : class
    {
        var entityType = context.Model.FindEntityType(typeof(T));
        if (entityType == null)
        {
            throw new InvalidOperationException(
                $"Entity type '{typeof(T).Name}' is not part of the DbContext model. " +
                "Ensure the entity is configured in your DbContext.");
        }

        var columns = new List<ColumnMetadata>();

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

            columns.Add(new ColumnMetadata
            {
                ColumnName = columnName,
                PropertyInfo = clrProperty,
                ClrType = Nullable.GetUnderlyingType(clrType) ?? clrType
            });
        }

        if (!includeIdentity && columns.Count == 0)
        {
            throw new InvalidOperationException(
                $"No valid columns found for entity type '{typeof(T).Name}'. " +
                "Ensure the entity has mapped properties.");
        }

        return columns;
    }

    /// <summary>
    /// Gets the full table name including schema.
    /// </summary>
    public static string GetTableName<T>(DbContext context) where T : class
    {
        var entityType = context.Model.FindEntityType(typeof(T));
        if (entityType == null)
        {
            throw new InvalidOperationException(
                $"Entity type '{typeof(T).Name}' is not part of the DbContext model.");
        }

        var tableName = entityType.GetTableName();
        var schema = entityType.GetSchema();

        if (string.IsNullOrEmpty(tableName))
        {
            throw new InvalidOperationException(
                $"Could not determine table name for entity type '{typeof(T).Name}'.");
        }

        return string.IsNullOrEmpty(schema)
            ? $"[{tableName}]"
            : $"[{schema}].[{tableName}]";
    }

    /// <summary>
    /// Checks if entity has identity column.
    /// </summary>
    public static bool HasIdentityColumn<T>(DbContext context) where T : class
    {
        var entityType = context.Model.FindEntityType(typeof(T));
        if (entityType == null)
            return false;

        return entityType.GetProperties()
            .Any(p => p.ValueGenerated == ValueGenerated.OnAdd &&
                     (p.GetValueGeneratorFactory() != null ||
                      p.GetDefaultValueSql() != null));
    }

}
