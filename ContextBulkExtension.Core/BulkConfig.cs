namespace ContextBulkExtension;

/// <summary>
/// Configuration options for bulk operations.
/// </summary>
public record BulkConfig
{
    /// <summary>
    /// Number of rows in each batch. Default is 10,000.
    /// </summary>
    public int BatchSize { get; set; } = 10000;

    /// <summary>
    /// Timeout in seconds for the bulk operation. Default is 300 seconds (5 minutes).
    /// </summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Check constraints during bulk insert. Default is true.
    /// </summary>
    public bool CheckConstraints { get; set; } = true;

    /// <summary>
    /// Fire insert triggers. Default is false for performance.
    /// </summary>
    public bool FireTriggers { get; set; } = false;

    /// <summary>
    /// Enable streaming for very large datasets. Default is true.
    /// </summary>
    public bool EnableStreaming { get; set; } = true;

    /// <summary>
    /// Use table lock for better performance. Default is true.
    /// Note: Set to false for memory-optimized tables (In-Memory OLTP).
    /// </summary>
    public bool UseTableLock { get; set; } = true;

    /// <summary>
    /// When true, only inserts new records and skips updates for existing records.
    /// Default is false (performs both insert and update operations).
    /// Use this when you use BulkUpsertAsync but doesn't want to update existing records.
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
