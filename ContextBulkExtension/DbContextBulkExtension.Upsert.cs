using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Text;

namespace ContextBulkExtension;

/// <summary>
/// Extension methods for DbContext to perform high-performance bulk upsert operations.
/// </summary>
public static partial class DbContextBulkExtensionUpsert
{
    /// <summary>
    /// Performs a high-performance bulk upsert (insert or update) of entities using SqlBulkCopy with MERGE statement.
    /// Inserts new records and updates existing records based on primary key matching.
    /// </summary>
    /// <typeparam name="T">The entity type</typeparam>
    /// <param name="context">The DbContext instance</param>
    /// <param name="entities">The entities to upsert</param>
    /// <exception cref="ArgumentNullException">Thrown when context or entities is null</exception>
    /// <exception cref="InvalidOperationException">Thrown when entity has no primary key, entity type is not part of the model, or database provider is not SQL Server</exception>
    public static async Task BulkUpsertAsync<T>(this DbContext context, IEnumerable<T> entities) where T : class
    {
        await BulkUpsertAsync(context, entities, new BulkUpsertOptions());
    }

    /// <summary>
    /// Performs a high-performance bulk upsert (insert or update) of entities using SqlBulkCopy with MERGE statement.
    /// Inserts new records and updates existing records based on primary key matching.
    /// </summary>
    /// <typeparam name="T">The entity type</typeparam>
    /// <param name="context">The DbContext instance</param>
    /// <param name="entities">The entities to upsert</param>
    /// <param name="options">Configuration options for the bulk upsert operation</param>
    /// <exception cref="ArgumentNullException">Thrown when context, entities, or options is null</exception>
    /// <exception cref="InvalidOperationException">Thrown when entity has no primary key, entity type is not part of the model, or database provider is not SQL Server</exception>
    public static async Task BulkUpsertAsync<T>(this DbContext context, IEnumerable<T> entities, BulkUpsertOptions options) where T : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(options);

        // Early return for empty collections
        if (entities.TryGetNonEnumeratedCount(out var count) && count == 0)
            return;

        // Get connection and validate SQL Server
        var dbConnection = context.Database.GetDbConnection();
        if (dbConnection is not SqlConnection connection)
        {
            throw new InvalidOperationException(
                $"BulkUpsertAsync only supports SQL Server. Current connection type: {dbConnection?.GetType().Name ?? "Unknown"}");
        }

        // Get metadata
        var primaryKeyColumns = EntityMetadataHelper.GetPrimaryKeyColumns<T>(context);
        if (primaryKeyColumns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Entity type '{typeof(T).Name}' has no primary key defined. " +
                "BulkUpsertAsync requires a primary key to match existing records.");
        }

        var columns = EntityMetadataHelper.GetColumnMetadata<T>(context, options.KeepIdentity);
        var tableName = EntityMetadataHelper.GetTableName<T>(context);

        // Ensure connection is open
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        // Generate unique temp table name to support concurrent operations
        var tempTableName = $"#TempStaging_{Guid.NewGuid():N}";

        try
        {
            // Get existing transaction if any
            var currentTransaction = context.Database.CurrentTransaction;
            SqlTransaction? sqlTransaction = null;

            if (currentTransaction != null)
            {
                sqlTransaction = currentTransaction.GetDbTransaction() as SqlTransaction;
            }

            // Step 1: Create temp staging table
            var createTempTableSql = BuildCreateTempTableSql(tempTableName, columns);
            using (var createCmd = new SqlCommand(createTempTableSql, connection, sqlTransaction))
            {
                await createCmd.ExecuteNonQueryAsync();
            }

            try
            {
                // Step 2: Bulk insert to temp table
                var bulkCopyOptions = SqlBulkCopyOptions.Default;

                if (options.KeepIdentity)
                    bulkCopyOptions |= SqlBulkCopyOptions.KeepIdentity;

                if (options.CheckConstraints)
                    bulkCopyOptions |= SqlBulkCopyOptions.CheckConstraints;

                // Don't use table lock on temp tables (not necessary and can cause issues)
                using var bulkCopy = new SqlBulkCopy(connection, bulkCopyOptions, sqlTransaction)
                {
                    DestinationTableName = tempTableName,
                    BatchSize = options.BatchSize,
                    BulkCopyTimeout = options.TimeoutSeconds,
                    EnableStreaming = options.EnableStreaming
                };

                // Map columns
                foreach (var column in columns)
                {
                    bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
                }

                // Bulk insert to temp table
                using var reader = new EntityDataReader<T>(entities, columns);
                await bulkCopy.WriteToServerAsync(reader);

                // Step 3: Execute MERGE statement
                var mergeSql = BuildMergeSql(tableName, tempTableName, columns, primaryKeyColumns, options);
                using var mergeCmd = new SqlCommand(mergeSql, connection, sqlTransaction);
                mergeCmd.CommandTimeout = options.TimeoutSeconds;
                await mergeCmd.ExecuteNonQueryAsync();
            }
            finally
            {
                // Step 4: Clean up temp table (ensure cleanup even on errors)
                try
                {
                    var dropTempTableSql = $"IF OBJECT_ID('tempdb..{tempTableName}') IS NOT NULL DROP TABLE {tempTableName};";
                    using var dropCmd = new SqlCommand(dropTempTableSql, connection, sqlTransaction);
                    await dropCmd.ExecuteNonQueryAsync();
                }
                catch
                {
                    // Temp tables are automatically cleaned up on connection close, so ignore errors
                }
            }
        }
        catch (SqlException ex)
        {
            throw new InvalidOperationException(
                $"Bulk upsert failed for entity type '{typeof(T).Name}'. " +
                $"Error: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Builds CREATE TABLE statement for temporary staging table.
    /// </summary>
    private static string BuildCreateTempTableSql(string tempTableName, IReadOnlyList<ColumnMetadata> columns)
    {
        var sql = new StringBuilder();
        sql.AppendLine($"CREATE TABLE {tempTableName} (");

        for (int i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            var sqlType = GetSqlType(column.ClrType);
            sql.Append($"    {EscapeSqlIdentifier(column.ColumnName)} {sqlType}");

            if (i < columns.Count - 1)
                sql.AppendLine(",");
            else
                sql.AppendLine();
        }

        sql.AppendLine(");");
        return sql.ToString();
    }

    /// <summary>
    /// Builds MERGE statement for upsert operation.
    /// </summary>
    private static string BuildMergeSql(
        string targetTableName,
        string sourceTableName,
        IReadOnlyList<ColumnMetadata> columns,
        IReadOnlyList<ColumnMetadata> primaryKeyColumns,
        BulkUpsertOptions options)
    {
        var sql = new StringBuilder();

        // MERGE statement header
        sql.AppendLine($"MERGE {targetTableName} AS target");
        sql.AppendLine($"USING {sourceTableName} AS source");

        // ON clause - match on primary key(s)
        sql.Append("ON ");
        for (int i = 0; i < primaryKeyColumns.Count; i++)
        {
            if (i > 0) sql.Append(" AND ");
            var pkColumn = EscapeSqlIdentifier(primaryKeyColumns[i].ColumnName);
            sql.Append($"target.{pkColumn} = source.{pkColumn}");
        }
        sql.AppendLine();

        // WHEN MATCHED clause (update)
        if (!options.InsertOnly)
        {
            // Determine which columns to update
            var updateColumns = columns
                .Where(c => !primaryKeyColumns.Any(pk => pk.ColumnName.Equals(c.ColumnName, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            // If UpdateColumns is specified, filter to only those columns
            if (options.UpdateColumns?.Count > 0)
            {
                updateColumns = [.. updateColumns.Where(c => options.UpdateColumns.Contains(c.ColumnName, StringComparer.OrdinalIgnoreCase))];
            }

            if (updateColumns.Count > 0)
            {
                sql.AppendLine("WHEN MATCHED THEN");
                sql.Append("    UPDATE SET ");

                for (int i = 0; i < updateColumns.Count; i++)
                {
                    if (i > 0) sql.Append(", ");
                    var columnName = EscapeSqlIdentifier(updateColumns[i].ColumnName);
                    sql.Append($"{columnName} = source.{columnName}");
                }
                sql.AppendLine();
            }
        }

        // WHEN NOT MATCHED clause (insert)
        sql.AppendLine("WHEN NOT MATCHED BY TARGET THEN");
        sql.Append("    INSERT (");

        for (int i = 0; i < columns.Count; i++)
        {
            if (i > 0) sql.Append(", ");
            sql.Append(EscapeSqlIdentifier(columns[i].ColumnName));
        }

        sql.AppendLine(")");
        sql.Append("    VALUES (");

        for (int i = 0; i < columns.Count; i++)
        {
            if (i > 0) sql.Append(", ");
            sql.Append($"source.{EscapeSqlIdentifier(columns[i].ColumnName)}");
        }

        sql.AppendLine(");");

        return sql.ToString();
    }

    /// <summary>
    /// Maps CLR types to SQL Server types for CREATE TABLE.
    /// </summary>
    private static string GetSqlType(Type clrType)
    {
        return clrType.Name switch
        {
            nameof(Boolean) => "BIT",
            nameof(Byte) => "TINYINT",
            nameof(Int16) => "SMALLINT",
            nameof(Int32) => "INT",
            nameof(Int64) => "BIGINT",
            nameof(Single) => "REAL",
            nameof(Double) => "FLOAT",
            nameof(Decimal) => "DECIMAL(18, 2)",
            nameof(DateTime) => "DATETIME2",
            nameof(DateTimeOffset) => "DATETIMEOFFSET",
            nameof(TimeSpan) => "TIME",
            nameof(Guid) => "UNIQUEIDENTIFIER",
            nameof(String) => "NVARCHAR(MAX)",
            "Byte[]" => "VARBINARY(MAX)",
            _ => "NVARCHAR(MAX)" // Default fallback
        };
    }

    /// <summary>
    /// Escapes SQL Server identifiers by replacing ] with ]] and wrapping in brackets.
    /// </summary>
    private static string EscapeSqlIdentifier(string identifier)
    {
        if (string.IsNullOrEmpty(identifier))
            throw new ArgumentException("SQL identifier cannot be null or empty.", nameof(identifier));

        // Replace ] with ]] (SQL Server escape sequence for brackets)
        return $"[{identifier.Replace("]", "]]")}]";
    }
}
