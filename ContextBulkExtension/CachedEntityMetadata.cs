namespace ContextBulkExtension;

/// <summary>
/// Cached metadata for an entity type to improve performance.
/// </summary>
internal class CachedEntityMetadata
{
    private readonly List<ColumnMetadata> _allColumns;

    /// <summary>
    /// All column metadata (single source of truth).
    /// </summary>
    public List<ColumnMetadata> AllColumns => _allColumns;

    /// <summary>
    /// Column metadata without identity columns (computed once in constructor).
    /// </summary>
    public List<ColumnMetadata> Columns { get; }

    /// <summary>
    /// Column metadata including identity columns (alias for AllColumns).
    /// </summary>
    public List<ColumnMetadata> ColumnsWithIdentity => _allColumns;

    /// <summary>
    /// Primary key column metadata (computed once in constructor).
    /// </summary>
    public List<ColumnMetadata> PrimaryKeyColumns { get; }

    /// <summary>
    /// Full table name including schema.
    /// </summary>
    public required string TableName { get; init; }

    public CachedEntityMetadata(List<ColumnMetadata> allColumns)
    {
        _allColumns = allColumns;
        Columns = allColumns.Where(c => !c.IsIdentity).ToList();
        PrimaryKeyColumns = allColumns.Where(c => c.IsPrimaryKey).ToList();
    }
}
