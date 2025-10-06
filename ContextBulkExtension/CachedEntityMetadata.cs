namespace ContextBulkExtension;

/// <summary>
/// Cached metadata for an entity type to improve performance.
/// </summary>
internal class CachedEntityMetadata
{
    /// <summary>
    /// Column metadata without identity columns.
    /// </summary>
    public required List<ColumnMetadata> Columns { get; init; }

    /// <summary>
    /// Column metadata including identity columns.
    /// </summary>
    public required List<ColumnMetadata> ColumnsWithIdentity { get; init; }

    /// <summary>
    /// Full table name including schema.
    /// </summary>
    public required string TableName { get; init; }

    /// <summary>
    /// Primary key column metadata.
    /// </summary>
    public required List<ColumnMetadata> PrimaryKeyColumns { get; init; }
}
