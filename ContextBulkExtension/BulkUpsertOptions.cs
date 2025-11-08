namespace ContextBulkExtension;

/// <summary>
/// Configuration options for bulk upsert operations.
/// </summary>
public record BulkUpsertOptions : BulkInsertOptions
{
    /// <summary>
    /// When true, only inserts new records and skips updates for existing records.
    /// Default is false (performs both insert and update operations).
    /// </summary>
    public bool InsertOnly { get; set; } = false;

    /// <summary>
    /// When true, syncs identity values back to the original entities after upsert operation.
    /// This is useful when matching on non-identity columns (e.g., Email, Username) and you need the identity values populated.
    /// - INSERT: Syncs newly generated identity values
    /// - UPDATE: Syncs existing identity values from database to entities
    /// Adds ~10-20% overhead for tracking and mapping identity values.
    /// Default is false (no identity synchronization).
    /// </summary>
    public bool IdentityOutput { get; set; } = false;
}
