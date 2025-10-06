namespace ContextBulkExtension;

/// <summary>
/// Configuration options for bulk upsert operations.
/// </summary>
public class BulkUpsertOptions : BulkInsertOptions
{
    /// <summary>
    /// Specifies which columns to update when a match is found.
    /// If null (default), all non-primary key columns will be updated.
    /// If specified, only these columns will be updated on matched rows.
    /// </summary>
    public List<string> UpdateColumns { get; set; } = [];

    /// <summary>
    /// When true, only inserts new records and skips updates for existing records.
    /// Default is false (performs both insert and update operations).
    /// </summary>
    public bool InsertOnly { get; set; } = false;
}
