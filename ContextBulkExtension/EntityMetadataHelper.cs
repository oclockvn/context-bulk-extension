using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Collections.Concurrent;
using System.Linq.Expressions;

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

            // Compile expression delegate for fast property access
            var compiledGetter = CompilePropertyGetter<T>(property, clrProperty);

            var columnMetadata = new ColumnMetadata
            {
                ColumnName = columnName,
                PropertyInfo = clrProperty,
                ClrType = Nullable.GetUnderlyingType(clrType) ?? clrType,
                CompiledGetter = compiledGetter
            };

            allColumns.Add(columnMetadata);

            // Exclude identity columns from non-identity list
            bool isIdentity = property.ValueGenerated == ValueGenerated.OnAdd &&
                             (property.GetDefaultValueSql()?.Contains("IDENTITY", StringComparison.OrdinalIgnoreCase) == true ||
                              property.GetValueGeneratorFactory() != null ||
                              (property.IsPrimaryKey() && (property.ClrType == typeof(int) || property.ClrType == typeof(long))));

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

    /// <summary>
    /// Compiles a fast property getter with support for complex properties and value converters.
    /// </summary>
    private static Func<object, object?> CompilePropertyGetter<T>(IProperty property, System.Reflection.PropertyInfo clrProperty) where T : class
    {
        var parameter = Expression.Parameter(typeof(object), "instance");
        Expression propertyAccess;

        // Check if this is a complex property (EF Core 8+)
        var complexProperty = property.DeclaringType as IComplexType;

        if (complexProperty != null)
        {
            // Handle nested complex property access: instance.ComplexProp.Property
            var complexPropInfo = complexProperty.ComplexProperty.PropertyInfo;
            if (complexPropInfo == null)
            {
                throw new InvalidOperationException($"Complex property '{complexProperty.ComplexProperty.Name}' has no PropertyInfo.");
            }

            var complexPropDeclaringType = complexPropInfo.DeclaringType!;

            // Cast instance to declaring type
            var typedInstance = complexPropDeclaringType.IsValueType
                ? Expression.Unbox(parameter, complexPropDeclaringType)
                : Expression.Convert(parameter, complexPropDeclaringType);

            // Access complex property: instance.ComplexProp
            var complexAccess = Expression.Property(typedInstance, complexPropInfo);

            // Access nested property: instance.ComplexProp.Property
            propertyAccess = Expression.Property(complexAccess, clrProperty);
        }
        else
        {
            // Simple property access: instance.Property
            var declaringType = clrProperty.DeclaringType!;

            // Use Unbox for value types, Convert for reference types
            var typedInstance = typeof(T).IsValueType
                ? Expression.Unbox(parameter, typeof(T))
                : Expression.Convert(parameter, typeof(T));

            propertyAccess = Expression.Property(typedInstance, clrProperty);
        }

        // Apply EF Core value converter if exists
        var converter = property.GetValueConverter();
        if (converter != null)
        {
            // Get the converter's ConvertToProvider method
            var convertMethod = converter.GetType().GetMethod("ConvertToProvider",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

            if (convertMethod != null)
            {
                var converterConstant = Expression.Constant(converter);
                Expression converterInput = propertyAccess;

                // Handle null for reference types
                if (propertyAccess.Type.IsClass)
                {
                    var nullCheck = Expression.Equal(propertyAccess, Expression.Constant(null, propertyAccess.Type));
                    var convertedValue = Expression.Call(converterConstant, convertMethod, propertyAccess);
                    var nullResult = Expression.Constant(null, convertedValue.Type);
                    propertyAccess = Expression.Condition(nullCheck, nullResult, convertedValue);
                }
                else
                {
                    propertyAccess = Expression.Call(converterConstant, convertMethod, propertyAccess);
                }
            }
        }

        // Only box value types, reference types don't need conversion
        var finalExpression = propertyAccess.Type.IsValueType
            ? Expression.Convert(propertyAccess, typeof(object))
            : (Expression)propertyAccess;

        return Expression.Lambda<Func<object, object?>>(finalExpression, parameter).Compile();
    }

}
