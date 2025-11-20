using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Collections.Concurrent;
using System.Linq.Expressions;

namespace ContextBulkExtension.Helpers;

/// <summary>
/// Helper class for extracting entity metadata from EF Core model.
/// </summary>
internal static class EntityMetadataHelper
{
    private static readonly ConcurrentDictionary<(Type EntityType, Type ContextType), CachedEntityMetadata> _cache = new();

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
        var convertiblePropertyColumnDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var convertibleColumnConverterDict = new Dictionary<string, ValueConverter>(StringComparer.OrdinalIgnoreCase);
        ValueConverter? identityColumnConverter = null;

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
            var propertyName = property.Name;

            // Check for owned entity properties (complex types)
            // Owned properties use composite key format: {NavigationName}_{PropertyName}
            if (property.DeclaringType is IComplexType complexProperty)
            {
                var navigationProperty = complexProperty.ComplexProperty;
                propertyName = $"{navigationProperty.Name}_{propertyName}";
            }

            // Get value converter if exists
            // Use GetTypeMapping().Converter instead of GetValueConverter() to detect all converters
            // including those from type mappings (e.g., strongly-typed IDs)
            var converter = property.GetTypeMapping().Converter;
            Type providerClrType;

            if (converter != null)
            {
                // Property has converter - track it in dictionaries
                convertiblePropertyColumnDict[propertyName] = columnName;
                convertibleColumnConverterDict[columnName] = converter;
                providerClrType = converter.ProviderClrType;
            }
            else
            {
                // No converter - use model type
                providerClrType = clrType;
            }

            // Use ProviderClrType exactly as defined by converter (preserve nullability)
            // SqlBulkCopy needs the exact type including nullability for correct column type mapping

            // Compile expression delegate for fast property access (without converter)
            var compiledGetter = CompilePropertyGetter<T>(property, clrProperty);
            var compiledSetter = CompilePropertySetter<T>(property, clrProperty);

            // Determine if this is a primary key
            bool isPrimaryKey = property.IsPrimaryKey();

            // Determine if this is an identity column
            bool isIdentity = property.ValueGenerated == ValueGenerated.OnAdd &&
                             (property.GetDefaultValueSql()?.Contains("IDENTITY", StringComparison.OrdinalIgnoreCase) == true ||
                              property.GetValueGeneratorFactory() != null ||
                              (isPrimaryKey && (property.ClrType == typeof(int) || property.ClrType == typeof(long))));

            // Track identity column converter
            if (isIdentity && converter != null && identityColumnConverter == null)
            {
                identityColumnConverter = converter;
            }

            var columnMetadata = new ColumnMetadata
            {
                ColumnName = columnName,
                SqlType = property.GetColumnType(),
                PropertyInfo = clrProperty,
                PropertyName = propertyName,
                ClrType = Nullable.GetUnderlyingType(clrType) ?? clrType,
                ProviderClrType = providerClrType,
                CompiledGetter = compiledGetter,
                CompiledSetter = compiledSetter,
                IsIdentity = isIdentity,
                IsPrimaryKey = isPrimaryKey,
                ValueConverter = converter
            };

            allColumns.Add(columnMetadata);
        }

        // Validate we have at least one non-identity column
        if (!allColumns.Any(c => !c.IsIdentity))
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
            ? EscapeSqlIdentifier(tableName)
            : $"{EscapeSqlIdentifier(schema)}.{EscapeSqlIdentifier(tableName)}";

        return new CachedEntityMetadata(allColumns)
        {
            TableName = fullTableName,
            ConvertiblePropertyColumnDict = convertiblePropertyColumnDict,
            ConvertibleColumnConverterDict = convertibleColumnConverterDict,
            IdentityColumnConverter = identityColumnConverter
        };
    }

    /// <summary>
    /// Extracts column metadata for bulk insert operations.
    /// </summary>
    /// <param name="context">The DbContext instance</param>
    /// <param name="includeIdentity">Whether to include identity columns. Default is false.</param>
    public static IReadOnlyList<ColumnMetadata> GetColumnMetadata<T>(DbContext context, bool includeIdentity = false) where T : class
    {
        var cacheKey = (typeof(T), context.GetType());

        var cached = _cache.GetOrAdd(cacheKey, _ => BuildEntityMetadata<T>(context));

        return includeIdentity ? cached.ColumnsWithIdentity : cached.Columns;
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
    /// Gets the primary key columns for an entity.
    /// </summary>
    public static IReadOnlyList<ColumnMetadata> GetPrimaryKeyColumns<T>(DbContext context) where T : class
    {
        var cacheKey = (typeof(T), context.GetType());

        var cached = _cache.GetOrAdd(cacheKey, _ => BuildEntityMetadata<T>(context));

        return cached.PrimaryKeyColumns;
    }

    /// <summary>
    /// Gets the identity columns for an entity.
    /// </summary>
    public static IReadOnlyList<ColumnMetadata> GetIdentityColumns<T>(DbContext context) where T : class
    {
        var cacheKey = (typeof(T), context.GetType());

        var cached = _cache.GetOrAdd(cacheKey, _ => BuildEntityMetadata<T>(context));

        return cached.ColumnsWithIdentity.Where(c => c.IsIdentity).ToList();
    }

    /// <summary>
	/// Gets the cached entity metadata including converter dictionaries.
	/// </summary>
	internal static CachedEntityMetadata GetCachedMetadata<T>(DbContext context) where T : class
    {
        var cacheKey = (typeof(T), context.GetType());
        return _cache.GetOrAdd(cacheKey, _ => BuildEntityMetadata<T>(context));
    }

    /// <summary>
    /// Clears the metadata cache. Useful for testing or when model changes at runtime.
    /// </summary>
    public static void ClearCache()
    {
        _cache.Clear();
    }

    /// <summary>
    /// Escapes SQL Server identifiers by replacing ] with ]] and wrapping in brackets.
    /// </summary>
    private static string EscapeSqlIdentifier(string identifier)
    {
        if (string.IsNullOrEmpty(identifier))
            throw new ArgumentException("SQL identifier cannot be null or empty.", nameof(identifier));

        // Replace ] with ]] (SQL Server escape sequence for brackets)
        return $"[{identifier.Replace("]", "]]")}]";
    }

    /// <summary>
	/// Compiles a fast property getter with support for complex properties.
	/// Returns raw model values (no converter applied - conversion happens at runtime).
    /// </summary>
    private static Func<object, object?> CompilePropertyGetter<T>(IProperty property, System.Reflection.PropertyInfo clrProperty) where T : class
    {
        var parameter = Expression.Parameter(typeof(object), "instance");
        Expression propertyAccess;

        // Check if this is a complex property (EF Core 8+)

        if (property.DeclaringType is IComplexType complexProperty)
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

        // Box value types for return
        if (propertyAccess.Type.IsValueType)
        {
            propertyAccess = Expression.Convert(propertyAccess, typeof(object));
        }

        // Final expression - propertyAccess should already be object? at this point
        var finalExpression = propertyAccess;

        return Expression.Lambda<Func<object, object?>>(finalExpression, parameter).Compile();
    }

    /// <summary>
	/// Compiles a fast property setter with support for complex properties.
	/// Accepts raw model values (no converter applied - conversion happens at runtime).
    /// </summary>
    private static Action<object, object?> CompilePropertySetter<T>(IProperty property, System.Reflection.PropertyInfo clrProperty) where T : class
    {
        var instanceParam = Expression.Parameter(typeof(object), "instance");
        var valueParam = Expression.Parameter(typeof(object), "value");

        Expression propertyAccess;
        Expression typedInstance;

        // Check if this is a complex property (EF Core 8+)

        if (property.DeclaringType is IComplexType complexProperty)
        {
            // Handle nested complex property access: instance.ComplexProp.Property
            var complexPropInfo = complexProperty.ComplexProperty.PropertyInfo;
            if (complexPropInfo == null)
            {
                throw new InvalidOperationException($"Complex property '{complexProperty.ComplexProperty.Name}' has no PropertyInfo.");
            }

            var complexPropDeclaringType = complexPropInfo.DeclaringType!;

            // Cast instance to declaring type
            typedInstance = complexPropDeclaringType.IsValueType
                ? Expression.Unbox(instanceParam, complexPropDeclaringType)
                : Expression.Convert(instanceParam, complexPropDeclaringType);

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
            typedInstance = typeof(T).IsValueType
                ? Expression.Unbox(instanceParam, typeof(T))
                : Expression.Convert(instanceParam, typeof(T));

            propertyAccess = Expression.Property(typedInstance, clrProperty);
        }

        // Convert value parameter to property type
        Expression convertedValue;
        var propertyType = clrProperty.PropertyType;

        // Handle nullable types
        if (propertyType.IsValueType && Nullable.GetUnderlyingType(propertyType) == null)
        {
            // Non-nullable value type: unbox
            convertedValue = Expression.Convert(valueParam, propertyType);
        }
        else
        {
            // Reference type or nullable: convert
            convertedValue = Expression.Convert(valueParam, propertyType);
        }

        // Create assignment: instance.Property = value
        var assignment = Expression.Assign(propertyAccess, convertedValue);

        return Expression.Lambda<Action<object, object?>>(assignment, instanceParam, valueParam).Compile();
    }

}

