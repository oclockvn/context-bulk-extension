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
    /// Inserts new records and updates existing records based on custom match columns or primary key matching.
    /// </summary>
    /// <typeparam name="T">The entity type</typeparam>
    /// <param name="context">The DbContext instance</param>
    /// <param name="entities">The entities to upsert</param>
    /// <param name="matchOn">Expression specifying which columns to match on. Use single property (x => x.Email) or anonymous type (x => new { x.Email, x.Username }). If null (default), primary keys will be used.</param>
    /// <param name="updateColumns">Expression specifying which columns to update on match. Use single property (x => x.Status) or anonymous type (x => new { x.Name, x.UpdatedAt }). If null (default), all non-key columns will be updated.</param>
    /// <param name="options">Configuration options for the bulk upsert operation. If null, default options will be used.</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <exception cref="ArgumentNullException">Thrown when context or entities is null</exception>
    /// <exception cref="InvalidOperationException">Thrown when entity has no primary key (and matchOn is null), entity type is not part of the model, or database provider is not SQL Server</exception>
    public static async Task BulkUpsertAsync<T>(this DbContext context, IEnumerable<T> entities, System.Linq.Expressions.Expression<Func<T, object>>? matchOn = null, System.Linq.Expressions.Expression<Func<T, object>>? updateColumns = null, BulkUpsertOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entities);

        options ??= new BulkUpsertOptions();

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

        // Determine which columns to match on
        IReadOnlyList<ColumnMetadata> matchColumns;

        if (matchOn != null)
        {
            // Extract property names from expression
            var propertyNames = ExtractPropertyNamesFromExpression(matchOn);

            // Get all columns and map property names to column metadata
            var allColumns = EntityMetadataHelper.GetColumnMetadata<T>(context, includeIdentity: true);
            matchColumns = allColumns
                .Where(c => propertyNames.Contains(c.PropertyInfo.Name))
                .ToList();

            // Validate all properties were found
            if (matchColumns.Count != propertyNames.Count)
            {
                var missing = propertyNames.Except(matchColumns.Select(c => c.PropertyInfo.Name));
                throw new InvalidOperationException(
                    $"Properties not found in entity metadata: {string.Join(", ", missing)}. " +
                    "Ensure the properties are mapped to database columns.");
            }
        }
        else
        {
            // Fall back to primary keys (existing behavior)
            matchColumns = EntityMetadataHelper.GetPrimaryKeyColumns<T>(context);

            if (matchColumns.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Entity type '{typeof(T).Name}' has no primary key defined. " +
                    "Either define a primary key or use matchOn parameter to specify custom match columns.");
            }
        }

        // For upsert, always include all columns (including identity) in temp table
        var columns = EntityMetadataHelper.GetColumnMetadata<T>(context, includeIdentity: true);
        var tableName = EntityMetadataHelper.GetTableName<T>(context);

        // Ensure connection is open
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

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
            var identityColumns = options.IdentityOutput ? EntityMetadataHelper.GetIdentityColumns<T>(context) : null;
            var needsIdentitySync = identityColumns?.Count > 0 && options.IdentityOutput;

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

                // Step 4: Execute MERGE statement with custom match columns
                var mergeSql = BuildMergeSql(tableName, tempTableName, columns, matchColumns, updateColumnNames, options, identityColumns);

                // Debug: Print generated SQL
                #if DEBUG
                System.Diagnostics.Debug.WriteLine("=== GENERATED MERGE SQL ===");
                System.Diagnostics.Debug.WriteLine(mergeSql);
                System.Diagnostics.Debug.WriteLine($"InsertOnly: {options.InsertOnly}");
                System.Diagnostics.Debug.WriteLine($"SyncIdentity: {options.IdentityOutput}");
                System.Diagnostics.Debug.WriteLine("=========================");
                #endif

                using var mergeCmd = new SqlCommand(mergeSql, connection, sqlTransaction);
                mergeCmd.CommandTimeout = options.TimeoutSeconds;

                // If identity sync is enabled, read OUTPUT results and sync back to entities
                if (needsIdentitySync)
                {
                    // Convert entities to list to access by index
                    var entitiesList = entities as IList<T> ?? [.. entities];

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
                            var entity = entitiesList[rowIndex];

                            // Set each identity column value (starting at index 1, after rowIndex)
                            for (int i = 0; i < identityColumns!.Count; i++)
                            {
                                var identityColumn = identityColumns[i];
                                var identityValue = outputReader.GetValue(i + 1);

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
            throw new InvalidOperationException(
                $"Bulk upsert failed for entity type '{typeof(T).Name}'. " +
                $"Error: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Builds CREATE TABLE statement for temporary staging table.
    /// </summary>
    private static string BuildCreateTempTableSql(string tempTableName, IReadOnlyList<ColumnMetadata> columns, bool includeRowIndex = false)
    {
        var sql = new StringBuilder();
        sql.AppendLine($"CREATE TABLE {tempTableName} (");

        // Add row index column as first column if requested
        if (includeRowIndex)
        {
            sql.AppendLine($"    [{BulkOperationConstants.RowIndexColumnName}] INT,");
        }

        for (int i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            sql.Append($"    {EscapeSqlIdentifier(column.ColumnName)} {column.SqlType}");

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
        BulkUpsertOptions options,
        IReadOnlyList<ColumnMetadata>? identityColumns = null)
    {
        var sql = new StringBuilder();

        // MERGE statement header
        sql.AppendLine($"MERGE {targetTableName} AS target");
        sql.AppendLine($"USING {sourceTableName} AS source");

        // ON clause - match on specified columns
        sql.Append("ON ");
        for (int i = 0; i < matchKeyColumns.Count; i++)
        {
            if (i > 0) sql.Append(" AND ");
            var matchColumn = EscapeSqlIdentifier(matchKeyColumns[i].ColumnName);
            sql.Append($"target.{matchColumn} = source.{matchColumn}");
        }
        sql.AppendLine();

        // WHEN MATCHED clause (update)
        if (!options.InsertOnly)
        {
            // Determine which columns to update (exclude identity columns and match columns)
            var updateColumns = columns
                .Where(c => !c.IsIdentity && !matchKeyColumns.Any(pk => pk.ColumnName.Equals(c.ColumnName, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            // If updateColumnNames is specified, filter to only those columns
            if (updateColumnNames?.Count > 0)
            {
                updateColumns = [.. updateColumns.Where(c => updateColumnNames.Contains(c.PropertyInfo.Name, StringComparer.OrdinalIgnoreCase))];
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
        // Always exclude identity columns from INSERT - let SQL Server auto-generate them
        var insertColumns = columns.Where(c => !c.IsIdentity).ToList();

        sql.AppendLine("WHEN NOT MATCHED BY TARGET THEN");
        sql.Append("    INSERT (");

        for (int i = 0; i < insertColumns.Count; i++)
        {
            if (i > 0) sql.Append(", ");
            sql.Append(EscapeSqlIdentifier(insertColumns[i].ColumnName));
        }

        sql.AppendLine(")");
        sql.Append("    VALUES (");

        for (int i = 0; i < insertColumns.Count; i++)
        {
            if (i > 0) sql.Append(", ");
            sql.Append($"source.{EscapeSqlIdentifier(insertColumns[i].ColumnName)}");
        }

        sql.AppendLine(")");

        // Add OUTPUT clause if identity sync is enabled and there are identity columns
        if (options.IdentityOutput && identityColumns?.Count > 0)
        {
            sql.Append($"OUTPUT source.[{BulkOperationConstants.RowIndexColumnName}]");

            // Output all identity columns
            foreach (var identityColumn in identityColumns)
            {
                sql.Append($", INSERTED.{EscapeSqlIdentifier(identityColumn.ColumnName)}");
            }

            sql.AppendLine($", {BulkOperationConstants.MergeActionColumn}");
        }

        sql.AppendLine(";");

        return sql.ToString();
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
            throw new ArgumentException(
                "Invalid MatchOn expression. Use either a single property (x => x.Email) " +
                "or anonymous type (x => new { x.Email, x.Username }).");
        }

        return propertyNames;
    }
}
