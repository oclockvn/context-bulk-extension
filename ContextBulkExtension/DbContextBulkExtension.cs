using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Data.SqlClient;
using System.Data;

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
    /// <exception cref="ArgumentNullException">Thrown when context or entities is null</exception>
    /// <exception cref="InvalidOperationException">Thrown when entity type is not part of the model or database provider is not SQL Server</exception>
    public static async Task BulkInsertAsync<T>(this DbContext context, IEnumerable<T> entities) where T : class
    {
        await BulkInsertAsync(context, entities, new BulkInsertOptions());
    }

    /// <summary>
    /// Performs a high-performance bulk insert of entities using SqlBulkCopy with custom options.
    /// Suitable for inserting millions of records efficiently.
    /// </summary>
    /// <typeparam name="T">The entity type</typeparam>
    /// <param name="context">The DbContext instance</param>
    /// <param name="entities">The entities to insert</param>
    /// <param name="options">Configuration options for the bulk insert operation</param>
    /// <exception cref="ArgumentNullException">Thrown when context, entities, or options is null</exception>
    /// <exception cref="InvalidOperationException">Thrown when entity type is not part of the model or database provider is not SQL Server</exception>
    public static async Task BulkInsertAsync<T>(this DbContext context, IEnumerable<T> entities, BulkInsertOptions options) where T : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(options);

        // Early return for empty collections
        if (!entities.Any())
            return;

        // Validate SQL Server provider
        var providerName = context.Database.ProviderName;
        if (providerName == null || !providerName.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"BulkInsertAsync only supports SQL Server. Current provider: {providerName ?? "Unknown"}");
        }

        // Get metadata
        var columns = EntityMetadataHelper.GetColumnMetadata<T>(context, options.KeepIdentity);

        var tableName = EntityMetadataHelper.GetTableName<T>(context);

        // Get connection
        var connection = context.Database.GetDbConnection() as SqlConnection;
        if (connection == null)
        {
            throw new InvalidOperationException("Could not retrieve SqlConnection from DbContext.");
        }

        // Ensure connection is open
        bool shouldCloseConnection = false;
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync();
            shouldCloseConnection = true;
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

            if (options.KeepIdentity)
                bulkCopyOptions |= SqlBulkCopyOptions.KeepIdentity;

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

            // Map columns
            foreach (var column in columns)
            {
                bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            }

            // Create data reader and perform bulk insert
            using var reader = new EntityDataReader<T>(entities, columns);
            await bulkCopy.WriteToServerAsync(reader);
        }
        catch (SqlException ex)
        {
            throw new InvalidOperationException(
                $"Bulk insert failed for entity type '{typeof(T).Name}'. " +
                $"Error: {ex.Message}", ex);
        }
        finally
        {
            if (shouldCloseConnection && connection.State == ConnectionState.Open)
            {
                await connection.CloseAsync();
            }
        }
    }
}
