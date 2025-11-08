using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Diagnostics;

namespace ContextBulkExtension;

/// <summary>
/// Extension methods for DbContext to perform high-performance bulk operations.
/// </summary>
public static class DbContextBulkExtension
{
    /// <summary>
    /// Performs a high-performance bulk insert of entities using SqlBulkCopy.
    /// Suitable for inserting millions of records efficiently.
    /// </summary>
    /// <typeparam name="T">The entity type</typeparam>
    /// <param name="context">The DbContext instance</param>
    /// <param name="entities">The entities to insert</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <exception cref="ArgumentNullException">Thrown when context or entities is null</exception>
    /// <exception cref="InvalidOperationException">Thrown when entity type is not part of the model or database provider is not SQL Server</exception>
    public static async Task BulkInsertAsync<T>(this DbContext context, IList<T> entities, CancellationToken cancellationToken = default) where T : class
    {
        await BulkInsertAsync(context, entities, new BulkInsertOptions(), cancellationToken);
    }

    /// <summary>
    /// Performs a high-performance bulk insert of entities using SqlBulkCopy with custom options.
    /// Suitable for inserting millions of records efficiently.
    /// </summary>
    /// <typeparam name="T">The entity type</typeparam>
    /// <param name="context">The DbContext instance</param>
    /// <param name="entities">The entities to insert</param>
    /// <param name="options">Configuration options for the bulk insert operation</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <exception cref="ArgumentNullException">Thrown when context, entities, or options is null</exception>
    /// <exception cref="InvalidOperationException">Thrown when entity type is not part of the model or database provider is not SQL Server</exception>
    public static async Task BulkInsertAsync<T>(this DbContext context, IList<T> entities, BulkInsertOptions options, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(options);

        // Early return for empty collections
        if (entities.Count == 0)
            return;

        // Get connection and validate SQL Server
        var dbConnection = context.Database.GetDbConnection();
        if (dbConnection is not SqlConnection connection)
        {
            throw new InvalidOperationException(
                $"BulkInsertAsync only supports SQL Server. Current connection type: {dbConnection?.GetType().Name ?? "Unknown"}");
        }

        // Get metadata (always exclude identity columns - let SQL Server auto-generate them)
        var columns = EntityMetadataHelper.GetColumnMetadata<T>(context, includeIdentity: false);

        var tableName = EntityMetadataHelper.GetTableName<T>(context);

        // Ensure connection is open (let EF Core/ADO.NET manage connection lifecycle)
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            // Get existing transaction if any
            var currentTransaction = context.Database.CurrentTransaction;
            SqlTransaction? sqlTransaction = null;

            if (currentTransaction != null)
            {
                sqlTransaction = currentTransaction.GetDbTransaction() as SqlTransaction;
            }

            // Configure SqlBulkCopy
            var bulkCopyOptions = SqlBulkCopyOptions.Default;

            if (options.CheckConstraints)
                bulkCopyOptions |= SqlBulkCopyOptions.CheckConstraints;

            if (options.FireTriggers)
                bulkCopyOptions |= SqlBulkCopyOptions.FireTriggers;

            if (options.UseTableLock)
                bulkCopyOptions |= SqlBulkCopyOptions.TableLock;

            using var bulkCopy = new SqlBulkCopy(connection, bulkCopyOptions, sqlTransaction)
            {
                DestinationTableName = tableName,
                BatchSize = options.BatchSize,
                BulkCopyTimeout = options.TimeoutSeconds,
                EnableStreaming = options.EnableStreaming
            };
            
            Debug.WriteLine($"[BULK] BulkInsertAsync inserting {entities.Count} entities into {tableName} with {columns.Count} columns");

            // Map columns
            foreach (var column in columns)
            {
                bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            }

            // Create data reader and perform bulk insert
            using var reader = new EntityDataReader<T>(entities, columns);
            await bulkCopy.WriteToServerAsync(reader, cancellationToken);
        }
        catch (SqlException ex)
        {
            throw new InvalidOperationException(
                $"Bulk insert failed for entity type '{typeof(T).Name}'. " +
                $"Error: {ex.Message}", ex);
        }
    }
}
