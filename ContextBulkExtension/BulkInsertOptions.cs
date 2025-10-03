using System.Data;

namespace ContextBulkExtension;

/// <summary>
/// Configuration options for bulk insert operations.
/// </summary>
public class BulkInsertOptions
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
    /// Preserve source identity values. Default is false.
    /// </summary>
    public bool KeepIdentity { get; set; } = false;

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
}
