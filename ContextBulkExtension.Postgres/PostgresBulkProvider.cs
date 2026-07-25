using System.Data.Common;
using System.Text;
using ContextBulkExtension.Core;
using ContextBulkExtension.Core.Abstractions;
using ContextBulkExtension.Core.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace ContextBulkExtension.Postgres;

internal sealed class PostgresBulkProvider : BulkProviderBase
{
    public override bool Supports(DbConnection connection) => connection is NpgsqlConnection;

    protected override bool OwnsTransactionWhenMissing => true;
    protected override bool BundlesDeleteInUpsert => false;
    protected override bool BundlesIdentityInUpsert => false;

    protected override string NewStagingTableName()
        => QuoteIdentifier($"temp_staging_{Guid.NewGuid():N}");

    protected override string BuildCreateStagingSql(string stagingTable, IReadOnlyList<ColumnMetadata> columns, bool includeRowIndex)
    {
        var sql = new StringBuilder(100 + columns.Count * 50);
        sql.AppendLine($"CREATE TEMP TABLE {stagingTable} (");

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

    protected override string BuildDropStagingSql(string stagingTable)
        => $"DROP TABLE IF EXISTS {stagingTable};";

    protected override async Task BulkLoadToTargetAsync<T>(
        DbContext context,
        DbConnection connection,
        string tableName,
        IReadOnlyList<ColumnMetadata> columns,
        IList<T> entities,
        BulkConfig config,
        CancellationToken cancellationToken)
    {
        // COPY enlists in connection's current EF/Npgsql transaction when present
        await CopyIntoAsync((NpgsqlConnection)connection, tableName, columns, entities, includeRowIndex: false, cancellationToken);
    }

    protected override async Task BulkLoadToStagingAsync<T>(
        DbContext context,
        DbConnection connection,
        string stagingTable,
        IReadOnlyList<ColumnMetadata> columns,
        IList<T> entities,
        bool includeRowIndex,
        BulkConfig config,
        CancellationToken cancellationToken)
    {
        await CopyIntoAsync((NpgsqlConnection)connection, stagingTable, columns, entities, includeRowIndex, cancellationToken);
    }

    protected override async Task ExecuteUpsertAsync<T>(
        UpsertRequest<T> request,
        CancellationToken cancellationToken)
    {
        var upsertSql = BuildUpsertSql(
            request.TargetTable,
            request.StagingTable,
            request.Columns,
            request.MatchColumns,
            request.UpdateColumnNames,
            request.Config,
            request.IdentityColumns);
        await using var upsertCmd = CreateCommand(
            request.Connection, request.Context, upsertSql, request.Config.TimeoutSeconds);
        await upsertCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    protected override async Task ExecuteDeleteNotMatchedAsync<T>(
        UpsertRequest<T> request,
        CancellationToken cancellationToken)
    {
        var deleteSql = BuildDeleteNotMatchedSql(
            request.TargetTable, request.StagingTable, request.MatchColumns, request.DeleteScopeSql);
        await using var deleteCmd = CreateCommand(
            request.Connection, request.Context, deleteSql, request.Config.TimeoutSeconds);
        if (request.DeleteScopeParameters?.Count > 0)
        {
            foreach (var p in request.DeleteScopeParameters)
                deleteCmd.Parameters.Add(p);
        }

        await deleteCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    protected override async Task SyncIdentitiesAsync<T>(
        UpsertRequest<T> request,
        CancellationToken cancellationToken)
    {
        var identityColumns = request.IdentityColumns!;
        var selectSql = BuildIdentitySelectSql(
            request.TargetTable, request.StagingTable, request.MatchColumns, identityColumns);
        await using var selectCmd = CreateCommand(
            request.Connection, request.Context, selectSql, request.Config.TimeoutSeconds);
        await using var reader = await selectCmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            BulkProviderHelpers.ApplyIdentityValues(reader, request.Entities, identityColumns);
    }

    protected override string AdaptDeleteScopeSql(string sqlServerStyleWhere)
        => BulkProviderHelpers.ToPostgresTargetQualified(sqlServerStyleWhere);

    protected override DbCommand CreateCommand(
        DbConnection connection,
        DbContext context,
        string sql,
        int timeoutSeconds)
    {
        var cmd = new NpgsqlCommand(sql, (NpgsqlConnection)connection)
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

    protected override bool IsProviderException(Exception ex) => ex is PostgresException;

    protected override Exception WrapProviderException(Exception ex, string operation, Type entityType)
        => new InvalidOperationException(
            $"Bulk {operation} failed for entity type '{entityType.Name}'. Error: {ex.Message}", ex);

    protected override string GetQualifiedTableName<T>(DbContext context)
        => GetQuotedTableName<T>(context);

    private async Task CopyIntoAsync<T>(
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

    private string BuildUpsertSql(
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

        if (!options.InsertOnly)
        {
            var updateCols = BulkProviderHelpers.FilterUpdateColumns(columns, matchKeyColumns, updateColumnNames);

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
            // Ceiling: ROW_NUMBER pairing assumes identity assigned in __RowIndex insert order (serial/bigserial).
            BulkProviderHelpers.EnsurePostgresIdentityOutputSupported(identityColumns!);
            var identities = identityColumns!;
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
            sql.AppendLine(string.Join(", ", identities.Select(c => QuoteIdentifier(c.ColumnName))));
            sql.AppendLine("),");
            sql.AppendLine("ins_ordered AS (");
            sql.Append("    SELECT ");
            sql.Append(string.Join(", ", identities.Select(c => QuoteIdentifier(c.ColumnName))));
            sql.Append(", ROW_NUMBER() OVER (ORDER BY ");
            sql.Append(QuoteIdentifier(identities[0].ColumnName));
            sql.AppendLine(") AS rn FROM ins");
            sql.AppendLine("),");
            sql.AppendLine("src_ordered AS (");
            sql.AppendLine($"    SELECT {rowIndex}, ROW_NUMBER() OVER (ORDER BY {rowIndex}) AS rn FROM to_insert");
            sql.AppendLine(")");
            sql.AppendLine($"UPDATE {sourceTableName} AS source SET");
            for (int i = 0; i < identities.Count; i++)
            {
                var col = QuoteIdentifier(identities[i].ColumnName);
                sql.Append($"    {col} = ins_ordered.{col}");
                sql.AppendLine(i < identities.Count - 1 ? "," : string.Empty);
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

    private string BuildDeleteNotMatchedSql(
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

    private string BuildIdentitySelectSql(
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

    protected override string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            throw new ArgumentException("SQL identifier cannot be null or empty.", nameof(identifier));

        return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private string GetQuotedTableName<T>(DbContext context) where T : class
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
