using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Text;
using System.Diagnostics;
using ContextBulkExtension.Helpers;
using ContextBulkExtension.Extensions;

namespace ContextBulkExtension;

/// <summary>
/// Extension methods for DbContext to perform high-performance bulk upsert operations.
/// </summary>
public static partial class DbContextBulkExtensionUpsert
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
        await BulkInsertAsync(context, entities, new BulkConfig(), cancellationToken);
    }

    /// <summary>
    /// Performs a high-performance bulk insert of entities using SqlBulkCopy with custom options.
    /// Suitable for inserting millions of records efficiently.
    /// </summary>
    /// <typeparam name="T">The entity type</typeparam>
    /// <param name="context">The DbContext instance</param>
    /// <param name="entities">The entities to insert</param>
	/// <param name="config">Configuration options for the bulk insert operation</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <exception cref="ArgumentNullException">Thrown when context, entities, or options is null</exception>
    /// <exception cref="InvalidOperationException">Thrown when entity type is not part of the model or database provider is not SQL Server</exception>
	public static async Task BulkInsertAsync<T>(this DbContext context, IList<T> entities, BulkConfig config, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(config);

        // Early return for empty collections
        if (entities.Count == 0)
            return;

        // If identity output is requested, use BulkUpsert with InsertOnly mode
        // This leverages the MERGE OUTPUT clause to retrieve generated identity values
        if (config.IdentityOutput)
        {
            var upsertConfig = new BulkConfig
            {
                BatchSize = config.BatchSize,
                TimeoutSeconds = config.TimeoutSeconds,
                EnableStreaming = config.EnableStreaming,
                UseTableLock = config.UseTableLock,
                CheckConstraints = config.CheckConstraints,
                FireTriggers = config.FireTriggers,
                IdentityOutput = true,
                InsertOnly = true  // Ensures no UPDATE clause in MERGE
            };

            await BulkUpsertInternalAsync(
                context,
                entities,
                matchOn: null,
                updateColumns: null,
                deleteScope: null,
                upsertConfig,
                deleteNotMatchedBySource: false,
                cancellationToken);
            return;
        }

        // Get connection and validate SQL Server
        var dbConnection = context.Database.GetDbConnection();
        if (dbConnection is not SqlConnection connection)
        {
            throw new InvalidOperationException(
                $"BulkInsertAsync only supports SQL Server. Current connection type: {dbConnection?.GetType().Name ?? "Unknown"}");
        }

        // Get metadata (always exclude identity columns - let SQL Server auto-generate them)
        var columns = EntityMetadataHelper.GetColumnMetadata<T>(context, includeIdentity: false);
        var cachedMetadata = EntityMetadataHelper.GetCachedMetadata<T>(context);
        var tableName = EntityMetadataHelper.GetTableName<T>(context);

        // Ensure connection is open using EF Core's connection management
        await context.Database.OpenConnectionAsync(cancellationToken);

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

            if (config.CheckConstraints)
                bulkCopyOptions |= SqlBulkCopyOptions.CheckConstraints;

            if (config.FireTriggers)
                bulkCopyOptions |= SqlBulkCopyOptions.FireTriggers;

            if (config.UseTableLock)
                bulkCopyOptions |= SqlBulkCopyOptions.TableLock;

            using var bulkCopy = new SqlBulkCopy(connection, bulkCopyOptions, sqlTransaction)
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
        catch (SqlException ex)
        {
            throw new InvalidOperationException(
                $"Bulk insert failed for entity type '{typeof(T).Name}'. " +
                $"Error: {ex.Message}", ex);
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Performs a high-performance bulk upsert (insert or update) of entities using SqlBulkCopy with MERGE statement.
    /// Inserts new records and updates existing records based on custom match columns or primary key matching.
    /// </summary>
    /// <typeparam name="T">The entity type</typeparam>
    /// <param name="context">The DbContext instance</param>
    /// <param name="entities">The entities to upsert</param>
    /// <param name="matchOn">Expression specifying which columns to match on. Use single property (x => x.Email) or anonymous type (x => new { x.Email, x.Username }). If null (default), primary keys will be used.</param>
    /// <param name="updateColumns">Expression specifying which columns to update on match. Use single property (x => x.Status) or anonymous type (x => new { x.Name, x.UpdatedAt }). If null (default), all non-key columns will be updated.</param>
	/// <param name="config">Configuration options for the bulk upsert operation. If null, default options will be used.</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <exception cref="ArgumentNullException">Thrown when context or entities is null</exception>
    /// <exception cref="InvalidOperationException">Thrown when entity has no primary key (and matchOn is null), entity type is not part of the model, or database provider is not SQL Server</exception>
    public static Task BulkUpsertAsync<T>(
        this DbContext context,
        IList<T> entities,
        System.Linq.Expressions.Expression<Func<T, object>>? matchOn = null,
        System.Linq.Expressions.Expression<Func<T, object>>? updateColumns = null,
        BulkConfig? config = null,
        CancellationToken cancellationToken = default) where T : class
        => BulkUpsertInternalAsync(context, entities, matchOn, updateColumns, deleteScope: null, config, deleteNotMatchedBySource: false, cancellationToken);

    /// <summary>
    /// Performs a high-performance bulk upsert (insert or update) of entities using SqlBulkCopy with MERGE statement.
    /// Inserts new records and updates existing records based on custom match columns or primary key matching.
    /// Additionally deletes records in the target table that don't exist in the source batch.
    /// <para>
    /// <strong>Note:</strong> If entities list is empty, the operation returns early without performing any deletions.
    /// Deletion requires at least one entity in the source batch to establish match criteria.
    /// </para>
    /// </summary>
    /// <typeparam name="T">The entity type</typeparam>
    /// <param name="context">The DbContext instance</param>
    /// <param name="entities">The entities to upsert</param>
    /// <param name="matchOn">Expression specifying which columns to match on. Use single property (x => x.Email) or anonymous type (x => new { x.Email, x.Username }). If null (default), primary keys will be used.</param>
    /// <param name="updateColumns">Expression specifying which columns to update on match. Use single property (x => x.Status) or anonymous type (x => new { x.Name, x.UpdatedAt }). If null (default), all non-key columns will be updated.</param>
    /// <param name="deleteScope">
    /// Optional expression to scope which records can be deleted.
	/// Example: x => x.DocumentId == 123
    /// <para>
    /// <strong>⚠️ CRITICAL:</strong> When deleteScope is null, ALL records in the target table
    /// that don't match ANY row in the source batch will be deleted. Use with extreme caution!
    /// </para>
    /// <para>
    /// <strong>Recommended usage:</strong> Always provide deleteScope to limit deletions to a specific subset of data
    /// (e.g., specific account, date range, or category).
    /// </para>
    /// </param>
    /// <param name="config">Configuration options for the bulk upsert operation. If null, default options will be used.</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <exception cref="ArgumentNullException">Thrown when context or entities is null</exception>
    /// <exception cref="InvalidOperationException">Thrown when entity has no primary key (and matchOn is null), entity type is not part of the model, or database provider is not SQL Server</exception>
    public static Task BulkUpsertWithDeleteScopeAsync<T>(
        this DbContext context,
        IList<T> entities,
        System.Linq.Expressions.Expression<Func<T, object>>? matchOn = null,
        System.Linq.Expressions.Expression<Func<T, object>>? updateColumns = null,
        System.Linq.Expressions.Expression<Func<T, bool>>? deleteScope = null,
        BulkConfig? config = null,
        CancellationToken cancellationToken = default) where T : class
        => BulkUpsertInternalAsync(context, entities, matchOn, updateColumns, deleteScope, config, deleteNotMatchedBySource: true, cancellationToken);

    /// <summary>
    /// Internal method that performs bulk upsert with optional delete when not matched by source.
    /// </summary>
    private static async Task BulkUpsertInternalAsync<T>(
        DbContext context,
        IList<T> entities,
        System.Linq.Expressions.Expression<Func<T, object>>? matchOn,
        System.Linq.Expressions.Expression<Func<T, object>>? updateColumns,
        System.Linq.Expressions.Expression<Func<T, bool>>? deleteScope,
        BulkConfig? config,
        bool deleteNotMatchedBySource,
        CancellationToken cancellationToken) where T : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entities);

        config ??= new BulkConfig();

        // Early return for empty collections
        if (entities.Count == 0)
            return;

        // Get connection and validate SQL Server
        var dbConnection = context.Database.GetDbConnection();
        if (dbConnection is not SqlConnection connection)
        {
            throw new InvalidOperationException(
                $"BulkUpsertAsync only supports SQL Server. Current connection type: {dbConnection?.GetType().Name ?? "Unknown"}");
        }

        // Determine which columns to match on
        IReadOnlyList<ColumnMetadata> matchColumns;

        if (matchOn != null)
        {
            // Extract property names from expression
            var propertyNames = ExtractPropertyNamesFromExpression(matchOn);

            // Get all columns and map property names to column metadata
            var allColumns = EntityMetadataHelper.GetColumnMetadata<T>(context, includeIdentity: true);

            // Use HashSet for O(1) lookup instead of O(n) List.Contains
            var propertyNamesSet = new HashSet<string>(propertyNames, StringComparer.OrdinalIgnoreCase);
            matchColumns = allColumns.Where(c => propertyNamesSet.Contains(c.PropertyInfo.Name)).ToList();

            // Validate all properties were found
            if (matchColumns.Count != propertyNames.Count)
            {
                var foundNames = new HashSet<string>(matchColumns.Select(c => c.PropertyInfo.Name), StringComparer.OrdinalIgnoreCase);
                var missing = propertyNames.Where(p => !foundNames.Contains(p));
                throw new InvalidOperationException($"Properties not found in entity metadata: {string.Join(", ", missing)}.");
            }
        }
        else
        {
            // Fall back to primary keys (existing behavior)
            matchColumns = EntityMetadataHelper.GetPrimaryKeyColumns<T>(context);

            if (matchColumns.Count == 0)
            {
                throw new InvalidOperationException($"Entity type '{typeof(T).Name}' has no primary key defined. Either define a primary key or use matchOn parameter to specify custom match columns.");
            }
        }

        // For upsert, always include all columns (including identity) in temp table
        var columns = EntityMetadataHelper.GetColumnMetadata<T>(context, includeIdentity: true);
        var cachedMetadata = EntityMetadataHelper.GetCachedMetadata<T>(context);
        var tableName = EntityMetadataHelper.GetTableName<T>(context);

        // Ensure connection is open using EF Core's connection management
        await context.Database.OpenConnectionAsync(cancellationToken);

        // Generate unique temp table name to support concurrent operations
        var tempTableName = $"{BulkOperationConstants.TempTablePrefix}{Guid.NewGuid():N}";

        try
        {
            // Get existing transaction if any
            var currentTransaction = context.Database.CurrentTransaction;
            SqlTransaction? sqlTransaction = null;

            if (currentTransaction != null)
            {
                sqlTransaction = currentTransaction.GetDbTransaction() as SqlTransaction;
            }

            // Check if we need to sync identity values
            var identityColumns = config.IdentityOutput ? EntityMetadataHelper.GetIdentityColumns<T>(context) : null;
            var needsIdentitySync = identityColumns?.Count > 0 && config.IdentityOutput;

            // Step 1: Create temp staging table
            var createTempTableSql = BuildCreateTempTableSql(tempTableName, columns, needsIdentitySync);
            using (var createCmd = new SqlCommand(createTempTableSql, connection, sqlTransaction))
            {
                await createCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            try
            {
                // Step 2: Bulk insert to temp table (always with KeepIdentity for temp table)
                var bulkCopyOptions = SqlBulkCopyOptions.KeepIdentity;

                if (config.UseTableLock)
                    bulkCopyOptions |= SqlBulkCopyOptions.TableLock;

                if (config.CheckConstraints)
                    bulkCopyOptions |= SqlBulkCopyOptions.CheckConstraints;

                using var bulkCopy = new SqlBulkCopy(connection, bulkCopyOptions, sqlTransaction)
                {
                    DestinationTableName = tempTableName,
                    BatchSize = config.BatchSize,
                    BulkCopyTimeout = config.TimeoutSeconds,
                    EnableStreaming = config.EnableStreaming
                };

                // Map columns (add row index mapping if needed)
                if (needsIdentitySync)
                {
                    bulkCopy.ColumnMappings.Add(BulkOperationConstants.RowIndexColumnName, BulkOperationConstants.RowIndexColumnName);
                }

                foreach (var column in columns)
                {
                    bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
                }

                // Bulk insert to temp table
                using var reader = new EntityDataReader<T>(entities, columns, needsIdentitySync);
                await bulkCopy.WriteToServerAsync(reader, cancellationToken);

                // Step 3: Extract update column names from expression (if provided)
                List<string>? updateColumnNames = null;
                if (updateColumns != null)
                {
                    updateColumnNames = ExtractPropertyNamesFromExpression(updateColumns);
                }

                // Step 4: Build deleteScope WHERE clause if provided
                string? deleteScopeSql = null;
                List<SqlParameter>? deleteScopeParameters = null;
                if (deleteNotMatchedBySource && deleteScope != null)
                {
                    (deleteScopeSql, deleteScopeParameters) = ExpressionHelper.BuildWhereClauseFromExpression(deleteScope, context);
                }

                // Step 5: Execute MERGE statement with custom match columns
                var mergeSql = BuildMergeSql(tableName, tempTableName, columns, matchColumns, updateColumnNames, config, identityColumns, deleteNotMatchedBySource, deleteScopeSql);

                // Debug: Print generated SQL
#if DEBUG
                Debug.WriteLine("=== GENERATED MERGE SQL ===");
                Debug.WriteLine($"[BULK] BulkUpsertAsync merging {entities.Count} entities into {tableName} with {columns.Count} columns, options: {config}");
                Debug.WriteLine(mergeSql);
                Debug.WriteLine("=========================");
#endif

                using var mergeCmd = new SqlCommand(mergeSql, connection, sqlTransaction);
                mergeCmd.CommandTimeout = config.TimeoutSeconds;

                // Add deleteScope parameters if any
                if (deleteScopeParameters?.Count > 0)
                {
                    mergeCmd.Parameters.AddRange([.. deleteScopeParameters]);
                }

                // If identity sync is enabled, read OUTPUT results and sync back to entities
                if (needsIdentitySync)
                {
                    // entitiesList already materialized above
                    using var outputReader = await mergeCmd.ExecuteReaderAsync(cancellationToken);

                    // Read OUTPUT results and sync identity values back to entities
                    while (await outputReader.ReadAsync(cancellationToken))
                    {
                        var rowIndex = outputReader.GetInt32(0);
                        var action = outputReader.GetString(outputReader.FieldCount - 1);

                        // Process both INSERT and UPDATE actions
                        // INSERT: newly created records get their generated identity
                        // UPDATE: existing records get their identity synced (useful when matching on non-identity columns)
                        if (action == BulkOperationConstants.MergeActionInsert || action == BulkOperationConstants.MergeActionUpdate)
                        {
                            var entity = entities[rowIndex];

                            // Set each identity column value (starting at index 1, after rowIndex)
                            for (int i = 0; i < identityColumns!.Count; i++)
                            {
                                var identityColumn = identityColumns[i];
                                var identityValue = outputReader.GetValue(i + 1);

                                // Apply ConvertFromProvider if identity column has converter
                                // Add defensive null check for DBNull.Value (unlikely but safer)
                                if (identityValue != null && identityValue != DBNull.Value && identityColumn.ValueConverter != null)
                                {
                                    identityValue = identityColumn.ValueConverter.ConvertFromProvider.Invoke(identityValue);
                                }

                                // Use compiled setter to set the identity value
                                identityColumn.CompiledSetter(entity, identityValue);
                            }
                        }
                    }
                }
                else
                {
                    await mergeCmd.ExecuteNonQueryAsync(cancellationToken);
                }
            }
            finally
            {
                // Step 4: Clean up temp table (ensure cleanup even on errors)
                try
                {
                    var dropTempTableSql = $"IF OBJECT_ID('tempdb..{tempTableName}') IS NOT NULL DROP TABLE {tempTableName};";
                    using var dropCmd = new SqlCommand(dropTempTableSql, connection, sqlTransaction);
                    await dropCmd.ExecuteNonQueryAsync(cancellationToken);
                }
                catch
                {
                    // Temp tables are automatically cleaned up on connection close, so ignore errors
                }
            }
        }
        catch (SqlException ex)
        {
            throw new InvalidOperationException($"Bulk upsert failed for entity type '{typeof(T).Name}'. Error: {ex.Message}", ex);
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Builds CREATE TABLE statement for temporary staging table.
    /// </summary>
    private static string BuildCreateTempTableSql(string tempTableName, IReadOnlyList<ColumnMetadata> columns, bool includeRowIndex = false)
    {
        // Pre-allocate StringBuilder capacity to avoid reallocations
        // Estimate: ~100 chars base + ~50 chars per column (column name + type + brackets/commas)
        var estimatedSize = 100 + (columns.Count * 50);
        var sql = new StringBuilder(estimatedSize);
        sql.AppendLine($"CREATE TABLE {tempTableName} (");

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
        if (!options.InsertOnly)
        {
            // Determine which columns to update (exclude identity columns and match columns)
            // Use HashSet for O(1) lookup instead of O(m) Any() per column
            var matchKeyColumnNames = new HashSet<string>(
                matchKeyColumns.Select(pk => pk.ColumnName),
                StringComparer.OrdinalIgnoreCase);

            var updateColumns = columns
                .Where(c => !c.IsIdentity && !matchKeyColumnNames.Contains(c.ColumnName))
                .ToList();

            // If updateColumnNames is specified, filter to only those columns
            if (updateColumnNames?.Count > 0)
            {
                var updateNamesSet = new HashSet<string>(updateColumnNames, StringComparer.OrdinalIgnoreCase);
                updateColumns = [.. updateColumns.Where(c => updateNamesSet.Contains(c.PropertyInfo.Name))];
            }

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
            }
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

        return sql.ToString();
    }

    /// <summary>
    /// Extracts property names from a MatchOn expression.
    /// Supports single property (x => x.Email) or anonymous type (x => new { x.Email, x.Username }).
    /// </summary>
    private static List<string> ExtractPropertyNamesFromExpression<T>(System.Linq.Expressions.Expression<Func<T, object>> expression)
    {
        var propertyNames = new List<string>();

        if (expression.Body is System.Linq.Expressions.NewExpression newExpression)
        {
            // Anonymous type: x => new { x.Email, x.Username }
            foreach (var arg in newExpression.Arguments)
            {
                if (arg is System.Linq.Expressions.MemberExpression memberExpr)
                {
                    propertyNames.Add(memberExpr.Member.Name);
                }
            }
        }
        else if (expression.Body is System.Linq.Expressions.MemberExpression memberExpression)
        {
            // Single property: x => x.Email
            propertyNames.Add(memberExpression.Member.Name);
        }
        else if (expression.Body is System.Linq.Expressions.UnaryExpression unaryExpression &&
                 unaryExpression.Operand is System.Linq.Expressions.MemberExpression unaryMember)
        {
            // Boxing conversion: x => (object)x.Id
            propertyNames.Add(unaryMember.Member.Name);
        }

        if (propertyNames.Count == 0)
        {
            throw new ArgumentException("Invalid MatchOn expression. Use either a single property (x => x.Email) or anonymous type (x => new { x.Email, x.Username }).");
        }

        return propertyNames;
    }
}
