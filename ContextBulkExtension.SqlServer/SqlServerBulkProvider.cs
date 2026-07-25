using ContextBulkExtension.Core;
using ContextBulkExtension.Core.Abstractions;
using ContextBulkExtension.Core.Extensions;
using ContextBulkExtension.Core.Helpers;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data.Common;
using System.Diagnostics;
using System.Text;

namespace ContextBulkExtension.SqlServer;

internal sealed class SqlServerBulkProvider : BulkProviderBase
{
    public override bool Supports(DbConnection connection) => connection is SqlConnection;

    protected override bool OwnsTransactionWhenMissing => false;
    protected override bool BundlesDeleteInUpsert => true;
    protected override bool BundlesIdentityInUpsert => true;

    protected override string QuoteIdentifier(string identifier) => identifier.EscapeSqlIdentifier();

    protected override string GetQualifiedTableName<T>(DbContext context) where T : class
        => EntityMetadataHelper.GetTableName<T>(context);

    protected override string NewStagingTableName()
        => $"{BulkOperationConstants.TempTablePrefix}{Guid.NewGuid():N}";

    /// <summary>
    /// Builds CREATE TABLE statement for temporary staging table.
    /// </summary>
    protected override string BuildCreateStagingSql(string stagingTable, IReadOnlyList<ColumnMetadata> columns, bool includeRowIndex)
    {
        // Pre-allocate StringBuilder capacity to avoid reallocations
        // Estimate: ~100 chars base + ~50 chars per column (column name + type + brackets/commas)
        var estimatedSize = 100 + (columns.Count * 50);
        var sql = new StringBuilder(estimatedSize);
        sql.AppendLine($"CREATE TABLE {stagingTable} (");

        // Add row index column as first column if requested
        if (includeRowIndex)
        {
            sql.AppendLine($"    [{BulkOperationConstants.RowIndexColumnName}] INT,");
        }

        for (int i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            sql.Append($"    {column.ColumnName.EscapeSqlIdentifier()} {column.SqlType}");

            if (i < columns.Count - 1)
                sql.AppendLine(",");
            else
                sql.AppendLine();
        }

        sql.AppendLine(");");
        return sql.ToString();
    }

    protected override string BuildDropStagingSql(string stagingTable)
        => $"IF OBJECT_ID('tempdb..{stagingTable}') IS NOT NULL DROP TABLE {stagingTable};";

    protected override async Task BulkLoadToTargetAsync<T>(
        DbContext context,
        DbConnection connection,
        string tableName,
        IReadOnlyList<ColumnMetadata> columns,
        IList<T> entities,
        BulkConfig config,
        CancellationToken cancellationToken) where T : class
    {
        var sqlConnection = (SqlConnection)connection;
        var sqlTransaction = GetSqlTransaction(context);

        // Configure SqlBulkCopy
        var bulkCopyOptions = SqlBulkCopyOptions.Default;

        if (config.CheckConstraints)
            bulkCopyOptions |= SqlBulkCopyOptions.CheckConstraints;

        if (config.FireTriggers)
            bulkCopyOptions |= SqlBulkCopyOptions.FireTriggers;

        if (config.UseTableLock)
            bulkCopyOptions |= SqlBulkCopyOptions.TableLock;

        using var bulkCopy = new SqlBulkCopy(sqlConnection, bulkCopyOptions, sqlTransaction)
        {
            DestinationTableName = tableName,
            BatchSize = config.BatchSize,
            BulkCopyTimeout = config.TimeoutSeconds,
            EnableStreaming = config.EnableStreaming
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

    protected override async Task BulkLoadToStagingAsync<T>(
        DbContext context,
        DbConnection connection,
        string stagingTable,
        IReadOnlyList<ColumnMetadata> columns,
        IList<T> entities,
        bool includeRowIndex,
        BulkConfig config,
        CancellationToken cancellationToken) where T : class
    {
        var sqlConnection = (SqlConnection)connection;
        var sqlTransaction = GetSqlTransaction(context);

        // Bulk insert to temp table (always with KeepIdentity for temp table)
        var bulkCopyOptions = SqlBulkCopyOptions.KeepIdentity;

        if (config.UseTableLock)
            bulkCopyOptions |= SqlBulkCopyOptions.TableLock;

        if (config.CheckConstraints)
            bulkCopyOptions |= SqlBulkCopyOptions.CheckConstraints;

        using var bulkCopy = new SqlBulkCopy(sqlConnection, bulkCopyOptions, sqlTransaction)
        {
            DestinationTableName = stagingTable,
            BatchSize = config.BatchSize,
            BulkCopyTimeout = config.TimeoutSeconds,
            EnableStreaming = config.EnableStreaming
        };

        // Map columns (add row index mapping if needed)
        if (includeRowIndex)
        {
            bulkCopy.ColumnMappings.Add(BulkOperationConstants.RowIndexColumnName, BulkOperationConstants.RowIndexColumnName);
        }

        foreach (var column in columns)
        {
            bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        }

        // Bulk insert to temp table
        using var reader = new EntityDataReader<T>(entities, columns, includeRowIndex);
        await bulkCopy.WriteToServerAsync(reader, cancellationToken);
    }

    protected override async Task ExecuteUpsertAsync<T>(
        UpsertRequest<T> request,
        CancellationToken cancellationToken) where T : class
    {
        var mergeSql = BuildMergeSql(
            request.TargetTable,
            request.StagingTable,
            request.Columns,
            request.MatchColumns,
            request.UpdateColumnNames,
            request.Config,
            request.IdentityColumns,
            request.BundleDeleteNotMatched,
            request.DeleteScopeSql);

        // Debug: Print generated SQL
#if DEBUG
        Debug.WriteLine("=== GENERATED MERGE SQL ===");
        Debug.WriteLine($"[BULK] BulkUpsertAsync merging {request.Entities.Count} entities into {request.TargetTable} with {request.Columns.Count} columns, options: {request.Config}");
        Debug.WriteLine(mergeSql);
        Debug.WriteLine("=========================");
#endif

        await using var mergeCmd = CreateCommand(
            request.Connection, request.Context, mergeSql, request.Config.TimeoutSeconds);

        // Add deleteScope parameters if any
        if (request.DeleteScopeParameters?.Count > 0)
        {
            foreach (var p in request.DeleteScopeParameters)
            {
                mergeCmd.Parameters.Add(p);
            }
        }

        // If identity sync is enabled, read OUTPUT results and sync back to entities
        if (request.NeedsIdentitySync)
        {
            using var outputReader = await mergeCmd.ExecuteReaderAsync(cancellationToken);

            // Read OUTPUT results and sync identity values back to entities
            while (await outputReader.ReadAsync(cancellationToken))
            {
                var action = outputReader.GetString(outputReader.FieldCount - 1);

                // Process both INSERT and UPDATE actions
                // INSERT: newly created records get their generated identity
                // UPDATE: existing records get their identity synced (useful when matching on non-identity columns)
                if (action == BulkOperationConstants.MergeActionInsert || action == BulkOperationConstants.MergeActionUpdate)
                    BulkProviderHelpers.ApplyIdentityValues(
                        outputReader, request.Entities, request.IdentityColumns!);
            }
        }
        else
        {
            await mergeCmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    protected override Task ExecuteDeleteNotMatchedAsync<T>(
        UpsertRequest<T> request,
        CancellationToken cancellationToken)
        => throw new NotSupportedException(
            "SqlServer bundles WHEN NOT MATCHED BY SOURCE delete into MERGE; ExecuteDeleteNotMatchedAsync must not be called.");

    protected override Task SyncIdentitiesAsync<T>(
        UpsertRequest<T> request,
        CancellationToken cancellationToken)
        => throw new NotSupportedException(
            "SqlServer bundles identity OUTPUT into MERGE; SyncIdentitiesAsync must not be called.");

    protected override string AdaptDeleteScopeSql(string sqlServerStyleWhere) => sqlServerStyleWhere;

    protected override DbCommand CreateCommand(
        DbConnection connection,
        DbContext context,
        string sql,
        int timeoutSeconds)
    {
        var cmd = new SqlCommand(sql, (SqlConnection)connection, GetSqlTransaction(context))
        {
            CommandTimeout = timeoutSeconds
        };
        return cmd;
    }

    protected override bool IsProviderException(Exception ex) => ex is SqlException;

    protected override Exception WrapProviderException(Exception ex, string operation, Type entityType)
        => new InvalidOperationException(
            $"Bulk {operation} failed for entity type '{entityType.Name}'. Error: {ex.Message}", ex);

    private static SqlTransaction? GetSqlTransaction(DbContext context)
    {
        var currentTransaction = context.Database.CurrentTransaction;
        if (currentTransaction == null)
            return null;

        return currentTransaction.GetDbTransaction() as SqlTransaction
            ?? throw new InvalidOperationException(
                "Current EF transaction is not a SqlTransaction. Bulk operations require the provider transaction type.");
    }

    /// <summary>
    /// Builds MERGE statement for upsert operation.
    /// </summary>
    private static string BuildMergeSql(
        string targetTableName,
        string sourceTableName,
        IReadOnlyList<ColumnMetadata> columns,
        IReadOnlyList<ColumnMetadata> matchKeyColumns,
        List<string>? updateColumnNames,
        BulkConfig options,
        IReadOnlyList<ColumnMetadata>? identityColumns = null,
        bool deleteNotMatchedBySource = false,
        string? deleteScopeSql = null)
    {
        // Pre-allocate StringBuilder capacity to avoid reallocations
        // Estimate: ~200 chars base + ~100 chars per column (MERGE has UPDATE SET and INSERT clauses)
        var estimatedSize = 200 + (columns.Count * 100);
        var sql = new StringBuilder(estimatedSize);

        // MERGE statement header
        sql.AppendLine($"MERGE {targetTableName} AS target");
        sql.AppendLine($"USING {sourceTableName} AS source");

        // ON clause - match on specified columns
        sql.Append("ON ");
        for (int i = 0; i < matchKeyColumns.Count; i++)
        {
            if (i > 0) sql.Append(" AND ");
            var matchColumn = matchKeyColumns[i].ColumnName.EscapeSqlIdentifier();
            sql.Append($"target.{matchColumn} = source.{matchColumn}");
        }
        sql.AppendLine();

        // WHEN MATCHED clause (update)
        var matchedClauseAdded = false;
        if (!options.InsertOnly)
        {
            var updateColumns = BulkProviderHelpers.FilterUpdateColumns(columns, matchKeyColumns, updateColumnNames);

            if (updateColumns.Count > 0)
            {
                sql.AppendLine("WHEN MATCHED THEN");
                sql.Append("    UPDATE SET ");

                for (int i = 0; i < updateColumns.Count; i++)
                {
                    if (i > 0) sql.Append(", ");
                    var columnName = updateColumns[i].ColumnName.EscapeSqlIdentifier();
                    sql.Append($"{columnName} = source.{columnName}");
                }
                sql.AppendLine();
                matchedClauseAdded = true;
            }
        }

        // ponytail: IdentityOutput needs OUTPUT rows for matched no-op paths; dummy UPDATE SET @dummy avoids touching table columns
        var needsDummyMatchedForIdentityOutput = !matchedClauseAdded
            && options.IdentityOutput
            && identityColumns?.Count > 0;

        if (needsDummyMatchedForIdentityOutput)
        {
            sql.AppendLine("WHEN MATCHED THEN");
            sql.AppendLine($"    UPDATE SET {BulkOperationConstants.MergeIdentitySyncDummyVariable} = 0");
        }

        // WHEN NOT MATCHED clause (insert)
        // Always exclude identity columns from INSERT - let SQL Server auto-generate them
        var insertColumns = columns.Where(c => !c.IsIdentity).ToList();

        sql.AppendLine("WHEN NOT MATCHED BY TARGET THEN");
        sql.Append("    INSERT (");

        for (int i = 0; i < insertColumns.Count; i++)
        {
            if (i > 0) sql.Append(", ");
            sql.Append(insertColumns[i].ColumnName.EscapeSqlIdentifier());
        }

        sql.AppendLine(")");
        sql.Append("    VALUES (");

        for (int i = 0; i < insertColumns.Count; i++)
        {
            if (i > 0) sql.Append(", ");
            sql.Append($"source.{insertColumns[i].ColumnName.EscapeSqlIdentifier()}");
        }

        sql.AppendLine(")");

        // WHEN NOT MATCHED BY SOURCE clause (delete)
        if (deleteNotMatchedBySource)
        {
            sql.Append("WHEN NOT MATCHED BY SOURCE");

            // Add deleteScope filter if provided
            if (!string.IsNullOrEmpty(deleteScopeSql))
            {
                sql.Append($" AND {deleteScopeSql}");
            }

            sql.AppendLine(" THEN");
            sql.AppendLine("    DELETE");
        }

        // Add OUTPUT clause if identity sync is enabled and there are identity columns
        if (options.IdentityOutput && identityColumns?.Count > 0)
        {
            sql.Append($"OUTPUT source.[{BulkOperationConstants.RowIndexColumnName}]");

            // Output all identity columns
            foreach (var identityColumn in identityColumns)
            {
                sql.Append($", INSERTED.{identityColumn.ColumnName.EscapeSqlIdentifier()}");
            }

            sql.AppendLine($", {BulkOperationConstants.MergeActionColumn}");
        }

        sql.AppendLine(";");

        if (needsDummyMatchedForIdentityOutput)
        {
            return $"DECLARE {BulkOperationConstants.MergeIdentitySyncDummyVariable} INT;{Environment.NewLine}{sql}";
        }

        return sql.ToString();
    }
}
