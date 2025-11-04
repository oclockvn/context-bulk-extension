namespace ContextBulkExtension;

/// <summary>
/// Configuration options for bulk upsert operations.
/// </summary>
public class BulkUpsertOptions : BulkInsertOptions
{
    /// <summary>
    /// When true, only inserts new records and skips updates for existing records.
    /// Default is false (performs both insert and update operations).
    /// </summary>
    public bool InsertOnly { get; set; } = false;

    /// <summary>
    /// When true, syncs auto-generated identity values back to the original entities after insert.
    /// Default is false (no identity synchronization).
    /// </summary>
    public bool IdentityOutput { get; set; } = false;
}
