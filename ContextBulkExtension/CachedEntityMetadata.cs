namespace ContextBulkExtension;

/// <summary>
/// Cached metadata for an entity type to improve performance.
/// </summary>
internal class CachedEntityMetadata(List<ColumnMetadata> allColumns)
{
    private readonly List<ColumnMetadata> _allColumns = allColumns;

    /// <summary>
    /// All column metadata (single source of truth).
    /// </summary>
    public IReadOnlyList<ColumnMetadata> AllColumns => _allColumns;

    /// <summary>
    /// Column metadata without identity columns (computed once in constructor).
    /// </summary>
    public IReadOnlyList<ColumnMetadata> Columns { get; } = [.. allColumns.Where(c => !c.IsIdentity)];

    /// <summary>
    /// Column metadata including identity columns (alias for AllColumns).
    /// </summary>
    public IReadOnlyList<ColumnMetadata> ColumnsWithIdentity => _allColumns;

    /// <summary>
    /// Primary key column metadata (computed once in constructor).
    /// </summary>
    public IReadOnlyList<ColumnMetadata> PrimaryKeyColumns { get; } = [.. allColumns.Where(c => c.IsPrimaryKey)];

    /// <summary>
    /// Full table name including schema.
    /// </summary>
    public required string TableName { get; init; }
}
