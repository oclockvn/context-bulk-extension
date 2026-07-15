using System.Data.Common;
using System.Linq.Expressions;
using System.Text;
using ContextBulkExtension.Core;
using ContextBulkExtension.Core.Abstractions;
using ContextBulkExtension.Core.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace ContextBulkExtension.PostgreSql;

internal sealed class PostgreSqlBulkProvider : IBulkProvider
{
    public bool Supports(DbConnection connection) => connection is NpgsqlConnection;

    public async Task BulkInsertAsync<T>(
        DbContext context,
        IList<T> entities,
        BulkConfig config,
        CancellationToken cancellationToken) where T : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(config);

        if (entities.Count == 0)
            return;

        var dbConnection = context.Database.GetDbConnection();
        if (dbConnection is not NpgsqlConnection connection)
        {
            throw new InvalidOperationException(
                $"BulkInsertAsync only supports PostgreSQL. Current connection type: {dbConnection?.GetType().Name ?? "Unknown"}");
        }

        var columns = EntityMetadataHelper.GetColumnMetadata<T>(context, includeIdentity: false);
        var tableName = GetQuotedTableName<T>(context);

        await context.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            // COPY enlists in connection's current EF/Npgsql transaction when present
            await CopyIntoAsync(connection, tableName, columns, entities, includeRowIndex: false, cancellationToken);
        }
        catch (PostgresException ex)
        {
            throw new InvalidOperationException(
                $"Bulk insert failed for entity type '{typeof(T).Name}'. Error: {ex.Message}", ex);
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    public async Task BulkUpsertAsync<T>(
        DbContext context,
        IList<T> entities,
        Expression<Func<T, object>>? matchOn,
        Expression<Func<T, object>>? updateColumns,
        Expression<Func<T, bool>>? deleteScope,
        BulkConfig config,
        bool deleteNotMatchedBySource,
        CancellationToken cancellationToken) where T : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(config);

        if (entities.Count == 0)
            return;

        var dbConnection = context.Database.GetDbConnection();
        if (dbConnection is not NpgsqlConnection connection)
        {
            throw new InvalidOperationException(
                $"BulkUpsertAsync only supports PostgreSQL. Current connection type: {dbConnection?.GetType().Name ?? "Unknown"}");
        }

        IReadOnlyList<ColumnMetadata> matchColumns;
        if (matchOn != null)
        {
            var propertyNames = ExpressionHelper.ExtractPropertyNamesFromExpression(matchOn);
            var allColumns = EntityMetadataHelper.GetColumnMetadata<T>(context, includeIdentity: true);
            var propertyNamesSet = new HashSet<string>(propertyNames, StringComparer.OrdinalIgnoreCase);
            matchColumns = allColumns.Where(c => propertyNamesSet.Contains(c.PropertyInfo.Name)).ToList();

            if (matchColumns.Count != propertyNames.Count)
            {
                var foundNames = new HashSet<string>(matchColumns.Select(c => c.PropertyInfo.Name), StringComparer.OrdinalIgnoreCase);
                var missing = propertyNames.Where(p => !foundNames.Contains(p));
                throw new InvalidOperationException($"Properties not found in entity metadata: {string.Join(", ", missing)}.");
            }
        }
        else
        {
            matchColumns = EntityMetadataHelper.GetPrimaryKeyColumns<T>(context);
            if (matchColumns.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Entity type '{typeof(T).Name}' has no primary key defined. Either define a primary key or use matchOn parameter to specify custom match columns.");
            }
        }

        var columns = EntityMetadataHelper.GetColumnMetadata<T>(context, includeIdentity: true);
        var tableName = GetQuotedTableName<T>(context);
        var identityColumns = config.IdentityOutput ? EntityMetadataHelper.GetIdentityColumns<T>(context) : null;
        var needsIdentitySync = identityColumns?.Count > 0 && config.IdentityOutput;
        var stagingTable = QuoteIdentifier($"temp_staging_{Guid.NewGuid():N}");

        await context.Database.OpenConnectionAsync(cancellationToken);

        // Own txn when none: UPDATE+INSERT (+ optional delete) must be atomic
        IDbContextTransaction? ownedTransaction = null;
        if (context.Database.CurrentTransaction == null)
            ownedTransaction = await context.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var createSql = BuildCreateTempTableSql(stagingTable, columns, needsIdentitySync);
            await using (var createCmd = CreateCommand(connection, context, createSql, config.TimeoutSeconds))
            {
                await createCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            try
            {
                await CopyIntoAsync(connection, stagingTable, columns, entities, needsIdentitySync, cancellationToken);

                List<string>? updateColumnNames = null;
                if (updateColumns != null)
                    updateColumnNames = ExpressionHelper.ExtractPropertyNamesFromExpression(updateColumns);

                var upsertSql = BuildUpsertSql(
                    tableName, stagingTable, columns, matchColumns, updateColumnNames, config, identityColumns);
                await using (var upsertCmd = CreateCommand(connection, context, upsertSql, config.TimeoutSeconds))
                {
                    await upsertCmd.ExecuteNonQueryAsync(cancellationToken);
                }

                if (deleteNotMatchedBySource)
                {
                    string? deleteScopeSql = null;
                    List<DbParameter>? deleteScopeParameters = null;
                    if (deleteScope != null)
                    {
                        (deleteScopeSql, deleteScopeParameters) = ExpressionHelper.BuildWhereClauseFromExpression(deleteScope, context);
                        deleteScopeSql = ToPostgresTargetQualified(deleteScopeSql);
                    }

                    var deleteSql = BuildDeleteNotMatchedSql(tableName, stagingTable, matchColumns, deleteScopeSql);
                    await using var deleteCmd = CreateCommand(connection, context, deleteSql, config.TimeoutSeconds);
                    if (deleteScopeParameters?.Count > 0)
                    {
                        foreach (var p in deleteScopeParameters)
                            deleteCmd.Parameters.Add(p);
                    }

                    await deleteCmd.ExecuteNonQueryAsync(cancellationToken);
                }

                if (needsIdentitySync)
                {
                    var selectSql = BuildIdentitySelectSql(tableName, stagingTable, matchColumns, identityColumns!);
                    await using var selectCmd = CreateCommand(connection, context, selectSql, config.TimeoutSeconds);
                    await using var reader = await selectCmd.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        var rowIndex = reader.GetInt32(0);
                        var entity = entities[rowIndex];
                        for (int i = 0; i < identityColumns!.Count; i++)
                        {
                            var identityColumn = identityColumns[i];
                            var identityValue = reader.GetValue(i + 1);
                            if (identityValue != null && identityValue != DBNull.Value && identityColumn.ValueConverter != null)
                                identityValue = identityColumn.ValueConverter.ConvertFromProvider.Invoke(identityValue);
                            identityColumn.CompiledSetter(entity, identityValue);
                        }
                    }
                }

                if (ownedTransaction != null)
                    await ownedTransaction.CommitAsync(cancellationToken);
            }
            finally
            {
                try
                {
                    await using var dropCmd = CreateCommand(connection, context, $"DROP TABLE IF EXISTS {stagingTable};", config.TimeoutSeconds);
                    await dropCmd.ExecuteNonQueryAsync(cancellationToken);
                }
                catch
                {
                    // temp tables cleaned on session end
                }
            }
        }
        catch (PostgresException ex)
        {
            if (ownedTransaction != null)
                await ownedTransaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException($"Bulk upsert failed for entity type '{typeof(T).Name}'. Error: {ex.Message}", ex);
        }
        catch
        {
            if (ownedTransaction != null)
                await ownedTransaction.RollbackAsync(cancellationToken);
            throw;
        }
        finally
        {
            if (ownedTransaction != null)
                await ownedTransaction.DisposeAsync();
            await context.Database.CloseConnectionAsync();
        }
    }

    private static NpgsqlCommand CreateCommand(NpgsqlConnection connection, DbContext context, string sql, int timeoutSeconds)
    {
        var cmd = new NpgsqlCommand(sql, connection)
        {
            CommandTimeout = timeoutSeconds
        };
        if (context.Database.CurrentTransaction != null)
        {
            cmd.Transaction = context.Database.CurrentTransaction.GetDbTransaction() as NpgsqlTransaction
                ?? throw new InvalidOperationException(
                    "Current EF transaction is not an NpgsqlTransaction. Bulk operations require the provider transaction type.");
        }
        return cmd;
    }

    private static async Task CopyIntoAsync<T>(
        NpgsqlConnection connection,
        string destinationTable,
        IReadOnlyList<ColumnMetadata> columns,
        IList<T> entities,
        bool includeRowIndex,
        CancellationToken cancellationToken) where T : class
    {
        var columnNames = new List<string>();
        if (includeRowIndex)
            columnNames.Add(QuoteIdentifier(BulkOperationConstants.RowIndexColumnName));
        columnNames.AddRange(columns.Select(c => QuoteIdentifier(c.ColumnName)));

        var copySql = $"COPY {destinationTable} ({string.Join(", ", columnNames)}) FROM STDIN (FORMAT BINARY)";
        await using var writer = await connection.BeginBinaryImportAsync(copySql, cancellationToken);

        for (int rowIndex = 0; rowIndex < entities.Count; rowIndex++)
        {
            var entity = entities[rowIndex];
            await writer.StartRowAsync(cancellationToken);

            if (includeRowIndex)
                await writer.WriteAsync(rowIndex, cancellationToken);

            foreach (var column in columns)
            {
                var value = column.CompiledGetter(entity);
                if (value != null && column.ValueConverter != null)
                    value = column.ValueConverter.ConvertToProvider.Invoke(value);

                await WriteCellAsync(writer, value, column.ProviderClrType, cancellationToken);
            }
        }

        await writer.CompleteAsync(cancellationToken);
    }

    private static string BuildCreateTempTableSql(string tempTableName, IReadOnlyList<ColumnMetadata> columns, bool includeRowIndex)
    {
        var sql = new StringBuilder(100 + columns.Count * 50);
        sql.AppendLine($"CREATE TEMP TABLE {tempTableName} (");

        if (includeRowIndex)
            sql.AppendLine($"    {QuoteIdentifier(BulkOperationConstants.RowIndexColumnName)} integer,");

        for (int i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            sql.Append($"    {QuoteIdentifier(column.ColumnName)} {column.SqlType}");
            sql.AppendLine(i < columns.Count - 1 ? "," : string.Empty);
        }

        sql.AppendLine(");");
        return sql.ToString();
    }

    private static string BuildUpsertSql(
        string targetTableName,
        string sourceTableName,
        IReadOnlyList<ColumnMetadata> columns,
        IReadOnlyList<ColumnMetadata> matchKeyColumns,
        List<string>? updateColumnNames,
        BulkConfig options,
        IReadOnlyList<ColumnMetadata>? identityColumns)
    {
        // ponytail: UPDATE+INSERT avoids ON CONFLICT + identity OVERRIDING when Id=0 on new rows.
        // Ceiling: not one-statement atomic vs concurrent writers (unique race); owned txn covers multi-statement only.
        var sql = new StringBuilder(300 + columns.Count * 100);
        var matchKeyColumnNames = new HashSet<string>(
            matchKeyColumns.Select(pk => pk.ColumnName),
            StringComparer.OrdinalIgnoreCase);

        if (!options.InsertOnly)
        {
            var updateCols = columns
                .Where(c => !c.IsIdentity && !matchKeyColumnNames.Contains(c.ColumnName))
                .ToList();

            if (updateColumnNames?.Count > 0)
            {
                var updateNamesSet = new HashSet<string>(updateColumnNames, StringComparer.OrdinalIgnoreCase);
                updateCols = [.. updateCols.Where(c => updateNamesSet.Contains(c.PropertyInfo.Name))];
            }

            if (updateCols.Count > 0)
            {
                sql.AppendLine($"UPDATE {targetTableName} AS target SET");
                for (int i = 0; i < updateCols.Count; i++)
                {
                    var col = QuoteIdentifier(updateCols[i].ColumnName);
                    sql.Append($"    {col} = source.{col}");
                    sql.AppendLine(i < updateCols.Count - 1 ? "," : string.Empty);
                }

                sql.AppendLine($"FROM {sourceTableName} AS source");
                sql.Append("WHERE ");
                for (int i = 0; i < matchKeyColumns.Count; i++)
                {
                    if (i > 0) sql.Append(" AND ");
                    var col = QuoteIdentifier(matchKeyColumns[i].ColumnName);
                    sql.Append($"target.{col} = source.{col}");
                }

                sql.AppendLine(";");
            }
        }

        var insertColumns = columns.Where(c => !c.IsIdentity).ToList();
        var writeIdentityToStaging = options.IdentityOutput && identityColumns?.Count > 0;
        var rowIndex = QuoteIdentifier(BulkOperationConstants.RowIndexColumnName);

        if (writeIdentityToStaging)
        {
            // INSERT…RETURNING → pair by __RowIndex order → UPDATE staging identity cols (map C)
            sql.AppendLine("WITH to_insert AS (");
            sql.Append($"    SELECT source.{rowIndex}");
            foreach (var col in insertColumns)
                sql.Append($", source.{QuoteIdentifier(col.ColumnName)}");
            sql.AppendLine();
            sql.AppendLine($"    FROM {sourceTableName} AS source");
            sql.AppendLine("    WHERE NOT EXISTS (");
            sql.AppendLine($"        SELECT 1 FROM {targetTableName} AS target");
            sql.Append("        WHERE ");
            for (int i = 0; i < matchKeyColumns.Count; i++)
            {
                if (i > 0) sql.Append(" AND ");
                var col = QuoteIdentifier(matchKeyColumns[i].ColumnName);
                sql.Append($"target.{col} = source.{col}");
            }

            sql.AppendLine();
            sql.AppendLine("    )");
            sql.AppendLine("),");
            sql.AppendLine("ins AS (");
            sql.Append($"    INSERT INTO {targetTableName} (");
            sql.Append(string.Join(", ", insertColumns.Select(c => QuoteIdentifier(c.ColumnName))));
            sql.AppendLine(")");
            sql.Append("    SELECT ");
            sql.Append(string.Join(", ", insertColumns.Select(c => QuoteIdentifier(c.ColumnName))));
            sql.AppendLine($" FROM to_insert ORDER BY {rowIndex}");
            sql.Append("    RETURNING ");
            sql.AppendLine(string.Join(", ", identityColumns!.Select(c => QuoteIdentifier(c.ColumnName))));
            sql.AppendLine("),");
            sql.AppendLine("ins_ordered AS (");
            sql.Append("    SELECT ");
            sql.Append(string.Join(", ", identityColumns.Select(c => QuoteIdentifier(c.ColumnName))));
            sql.Append(", ROW_NUMBER() OVER (ORDER BY ");
            sql.Append(QuoteIdentifier(identityColumns[0].ColumnName));
            sql.AppendLine(") AS rn FROM ins");
            sql.AppendLine("),");
            sql.AppendLine("src_ordered AS (");
            sql.AppendLine($"    SELECT {rowIndex}, ROW_NUMBER() OVER (ORDER BY {rowIndex}) AS rn FROM to_insert");
            sql.AppendLine(")");
            sql.AppendLine($"UPDATE {sourceTableName} AS source SET");
            for (int i = 0; i < identityColumns.Count; i++)
            {
                var col = QuoteIdentifier(identityColumns[i].ColumnName);
                sql.Append($"    {col} = ins_ordered.{col}");
                sql.AppendLine(i < identityColumns.Count - 1 ? "," : string.Empty);
            }

            sql.AppendLine("FROM src_ordered");
            sql.AppendLine("INNER JOIN ins_ordered ON src_ordered.rn = ins_ordered.rn");
            sql.AppendLine($"WHERE source.{rowIndex} = src_ordered.{rowIndex};");
        }
        else
        {
            sql.Append($"INSERT INTO {targetTableName} (");
            sql.Append(string.Join(", ", insertColumns.Select(c => QuoteIdentifier(c.ColumnName))));
            sql.AppendLine(")");
            sql.Append("SELECT ");
            sql.Append(string.Join(", ", insertColumns.Select(c => $"source.{QuoteIdentifier(c.ColumnName)}")));
            sql.AppendLine($" FROM {sourceTableName} AS source");
            sql.AppendLine("WHERE NOT EXISTS (");
            sql.AppendLine($"    SELECT 1 FROM {targetTableName} AS target");
            sql.Append("    WHERE ");
            for (int i = 0; i < matchKeyColumns.Count; i++)
            {
                if (i > 0) sql.Append(" AND ");
                var col = QuoteIdentifier(matchKeyColumns[i].ColumnName);
                sql.Append($"target.{col} = source.{col}");
            }

            sql.AppendLine();
            sql.AppendLine(");");
        }

        return sql.ToString();
    }

    private static string BuildDeleteNotMatchedSql(
        string targetTableName,
        string sourceTableName,
        IReadOnlyList<ColumnMetadata> matchKeyColumns,
        string? deleteScopeSql)
    {
        var sql = new StringBuilder();
        sql.AppendLine($"DELETE FROM {targetTableName} AS target");
        sql.Append("WHERE ");

        if (!string.IsNullOrEmpty(deleteScopeSql))
            sql.Append($"({deleteScopeSql}) AND ");

        sql.AppendLine("NOT EXISTS (");
        sql.AppendLine($"    SELECT 1 FROM {sourceTableName} AS source");
        sql.Append("    WHERE ");
        for (int i = 0; i < matchKeyColumns.Count; i++)
        {
            if (i > 0) sql.Append(" AND ");
            var col = QuoteIdentifier(matchKeyColumns[i].ColumnName);
            sql.Append($"target.{col} = source.{col}");
        }

        sql.AppendLine();
        sql.AppendLine(");");
        return sql.ToString();
    }

    private static string BuildIdentitySelectSql(
        string targetTableName,
        string sourceTableName,
        IReadOnlyList<ColumnMetadata> matchKeyColumns,
        IReadOnlyList<ColumnMetadata> identityColumns)
    {
        var sql = new StringBuilder();
        sql.Append($"SELECT source.{QuoteIdentifier(BulkOperationConstants.RowIndexColumnName)}");
        foreach (var identityColumn in identityColumns)
            sql.Append($", target.{QuoteIdentifier(identityColumn.ColumnName)}");
        sql.AppendLine();
        sql.AppendLine($"FROM {sourceTableName} AS source");
        sql.AppendLine($"INNER JOIN {targetTableName} AS target");
        sql.Append("ON ");
        for (int i = 0; i < matchKeyColumns.Count; i++)
        {
            if (i > 0) sql.Append(" AND ");
            var col = QuoteIdentifier(matchKeyColumns[i].ColumnName);
            sql.Append($"target.{col} = source.{col}");
        }

        sql.AppendLine(";");
        return sql.ToString();
    }

    // ExpressionHelper emits SQL Server bracket identifiers; map to Postgres quotes for deleteScope
    private static string ToPostgresTargetQualified(string sqlServerStyleWhere)
        => sqlServerStyleWhere.Replace("[", "\"", StringComparison.Ordinal).Replace("]", "\"", StringComparison.Ordinal);

    internal static string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            throw new ArgumentException("SQL identifier cannot be null or empty.", nameof(identifier));

        return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static string GetQuotedTableName<T>(DbContext context) where T : class
    {
        var entityType = context.Model.FindEntityType(typeof(T))
            ?? throw new InvalidOperationException($"Entity type '{typeof(T).Name}' is not part of the DbContext model.");

        var tableName = entityType.GetTableName()
            ?? throw new InvalidOperationException($"Could not determine table name for entity type '{typeof(T).Name}'.");

        var schema = entityType.GetSchema();
        return string.IsNullOrEmpty(schema)
            ? QuoteIdentifier(tableName)
            : $"{QuoteIdentifier(schema)}.{QuoteIdentifier(tableName)}";
    }

    private static async Task WriteCellAsync(
        NpgsqlBinaryImporter writer,
        object? value,
        Type providerClrType,
        CancellationToken cancellationToken)
    {
        if (value is null || value is DBNull)
        {
            await writer.WriteNullAsync(cancellationToken);
            return;
        }

        var targetType = Nullable.GetUnderlyingType(providerClrType) ?? providerClrType;
        if (value.GetType() != targetType && value is IConvertible && targetType != typeof(Guid) && !targetType.IsEnum)
            value = Convert.ChangeType(value, targetType);

        switch (value)
        {
            case int i:
                await writer.WriteAsync(i, cancellationToken);
                break;
            case long l:
                await writer.WriteAsync(l, cancellationToken);
                break;
            case short s:
                await writer.WriteAsync(s, cancellationToken);
                break;
            case byte b:
                await writer.WriteAsync(b, cancellationToken);
                break;
            case bool bo:
                await writer.WriteAsync(bo, cancellationToken);
                break;
            case string str:
                await writer.WriteAsync(str, cancellationToken);
                break;
            case DateTime dt:
                // Npgsql timestamptz requires UTC Kind
                if (dt.Kind == DateTimeKind.Unspecified)
                    dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                else if (dt.Kind == DateTimeKind.Local)
                    dt = dt.ToUniversalTime();
                await writer.WriteAsync(dt, cancellationToken);
                break;
            case DateTimeOffset dto:
                await writer.WriteAsync(dto, cancellationToken);
                break;
            case decimal dec:
                await writer.WriteAsync(dec, cancellationToken);
                break;
            case double dbl:
                await writer.WriteAsync(dbl, cancellationToken);
                break;
            case float fl:
                await writer.WriteAsync(fl, cancellationToken);
                break;
            case Guid g:
                await writer.WriteAsync(g, cancellationToken);
                break;
            case byte[] bytes:
                await writer.WriteAsync(bytes, cancellationToken);
                break;
            default:
                throw new NotSupportedException(
                    $"PostgreSQL binary COPY does not support CLR type '{value.GetType().FullName}'. Convert to a supported provider type.");
        }
    }
}
