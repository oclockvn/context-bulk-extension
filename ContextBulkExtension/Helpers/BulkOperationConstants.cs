namespace ContextBulkExtension.Helpers;

/// <summary>
/// Constants used throughout bulk operations.
/// </summary>
internal static class BulkOperationConstants
{
    /// <summary>
    /// Column name used for tracking row indices in temporary staging tables.
    /// </summary>
    public const string RowIndexColumnName = "__RowIndex";

    /// <summary>
    /// SQL data type for the row index column.
    /// </summary>
    public const string RowIndexColumnType = "INT";

    /// <summary>
    /// Prefix for temporary staging table names.
    /// </summary>
    public const string TempTablePrefix = "#TempStaging_";

    /// <summary>
    /// SQL action returned by MERGE OUTPUT clause for insert operations.
    /// </summary>
    public const string MergeActionInsert = "INSERT";

    /// <summary>
    /// SQL action returned by MERGE OUTPUT clause for update operations.
    /// </summary>
    public const string MergeActionUpdate = "UPDATE";

    /// <summary>
    /// SQL action returned by MERGE OUTPUT clause for delete operations.
    /// </summary>
    public const string MergeActionDelete = "DELETE";

    /// <summary>
    /// Name of the $action pseudo-column in MERGE OUTPUT clause.
    /// </summary>
    public const string MergeActionColumn = "$action";
}
