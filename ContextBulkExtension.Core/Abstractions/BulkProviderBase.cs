using System.Data;
using System.Data.Common;
using System.Linq.Expressions;
using ContextBulkExtension.Core.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ContextBulkExtension.Core.Abstractions;

/// <summary>
/// Template-method base: shared insert/upsert orchestration; providers supply dialect hooks.
/// </summary>
internal abstract class BulkProviderBase : IBulkProvider
{
    public abstract bool Supports(DbConnection connection);

    protected abstract bool OwnsTransactionWhenMissing { get; }
    protected abstract bool BundlesDeleteInUpsert { get; }
    protected abstract bool BundlesIdentityInUpsert { get; }

    protected abstract string QuoteIdentifier(string identifier);
    protected abstract string GetQualifiedTableName<T>(DbContext context) where T : class;
    protected abstract string NewStagingTableName();
    protected abstract string BuildCreateStagingSql(string stagingTable, IReadOnlyList<ColumnMetadata> columns, bool includeRowIndex);
    protected abstract string BuildDropStagingSql(string stagingTable);

    protected abstract Task BulkLoadToTargetAsync<T>(
        DbContext context,
        DbConnection connection,
        string tableName,
        IReadOnlyList<ColumnMetadata> columns,
        IList<T> entities,
        BulkConfig config,
        CancellationToken cancellationToken) where T : class;

    protected abstract Task BulkLoadToStagingAsync<T>(
        DbContext context,
        DbConnection connection,
        string stagingTable,
        IReadOnlyList<ColumnMetadata> columns,
        IList<T> entities,
        bool includeRowIndex,
        BulkConfig config,
        CancellationToken cancellationToken) where T : class;

    protected abstract Task ExecuteUpsertAsync<T>(
        DbContext context,
        DbConnection connection,
        string targetTable,
        string stagingTable,
        IReadOnlyList<ColumnMetadata> columns,
        IReadOnlyList<ColumnMetadata> matchColumns,
        List<string>? updateColumnNames,
        BulkConfig config,
        IReadOnlyList<ColumnMetadata>? identityColumns,
        bool needsIdentitySync,
        bool deleteNotMatchedBySource,
        string? deleteScopeSql,
        List<DbParameter>? deleteScopeParameters,
        IList<T> entities,
        CancellationToken cancellationToken) where T : class;

    protected abstract Task ExecuteDeleteNotMatchedAsync(
        DbContext context,
        DbConnection connection,
        string targetTable,
        string stagingTable,
        IReadOnlyList<ColumnMetadata> matchColumns,
        string? deleteScopeSql,
        List<DbParameter>? deleteScopeParameters,
        BulkConfig config,
        CancellationToken cancellationToken);

    protected abstract Task SyncIdentitiesAsync<T>(
        DbContext context,
        DbConnection connection,
        string targetTable,
        string stagingTable,
        IReadOnlyList<ColumnMetadata> matchColumns,
        IReadOnlyList<ColumnMetadata> identityColumns,
        IList<T> entities,
        BulkConfig config,
        CancellationToken cancellationToken) where T : class;

    protected abstract string AdaptDeleteScopeSql(string sqlServerStyleWhere);

    protected abstract DbCommand CreateCommand(
        DbConnection connection,
        DbContext context,
        string sql,
        int timeoutSeconds);

    protected abstract bool IsProviderException(Exception ex);

    protected abstract Exception WrapProviderException(Exception ex, string operation, Type entityType);

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

        var connection = context.Database.GetDbConnection();
        if (!Supports(connection))
        {
            throw new InvalidOperationException(
                $"BulkInsertAsync does not support connection type: {connection?.GetType().Name ?? "Unknown"}");
        }

        var columns = EntityMetadataHelper.GetColumnMetadata<T>(context, includeIdentity: false);
        var tableName = GetQualifiedTableName<T>(context);

        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await BulkLoadToTargetAsync(context, connection, tableName, columns, entities, config, cancellationToken);
        }
        catch (Exception ex) when (IsProviderException(ex))
        {
            throw WrapProviderException(ex, "insert", typeof(T));
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

        var connection = context.Database.GetDbConnection();
        if (!Supports(connection))
        {
            throw new InvalidOperationException(
                $"BulkUpsertAsync does not support connection type: {connection?.GetType().Name ?? "Unknown"}");
        }

        var matchColumns = BulkProviderHelpers.ResolveMatchColumns(context, matchOn);
        var columns = EntityMetadataHelper.GetColumnMetadata<T>(context, includeIdentity: true);
        var tableName = GetQualifiedTableName<T>(context);
        var identityColumns = config.IdentityOutput ? EntityMetadataHelper.GetIdentityColumns<T>(context) : null;
        var needsIdentitySync = identityColumns?.Count > 0 && config.IdentityOutput;
        var stagingTable = NewStagingTableName();

        await context.Database.OpenConnectionAsync(cancellationToken);

        IDbContextTransaction? ownedTransaction = null;
        if (OwnsTransactionWhenMissing && context.Database.CurrentTransaction == null)
            ownedTransaction = await context.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            await using (var createCmd = CreateCommand(connection, context, BuildCreateStagingSql(stagingTable, columns, needsIdentitySync), config.TimeoutSeconds))
            {
                await createCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            try
            {
                await BulkLoadToStagingAsync(
                    context, connection, stagingTable, columns, entities, needsIdentitySync, config, cancellationToken);

                List<string>? updateColumnNames = null;
                if (updateColumns != null)
                    updateColumnNames = ExpressionHelper.ExtractPropertyNamesFromExpression(updateColumns);

                string? deleteScopeSql = null;
                List<DbParameter>? deleteScopeParameters = null;
                if (deleteNotMatchedBySource && deleteScope != null)
                {
                    (deleteScopeSql, deleteScopeParameters) = ExpressionHelper.BuildWhereClauseFromExpression(deleteScope, context);
                    deleteScopeSql = AdaptDeleteScopeSql(deleteScopeSql);
                }

                await ExecuteUpsertAsync(
                    context,
                    connection,
                    tableName,
                    stagingTable,
                    columns,
                    matchColumns,
                    updateColumnNames,
                    config,
                    identityColumns,
                    needsIdentitySync,
                    deleteNotMatchedBySource && BundlesDeleteInUpsert,
                    deleteScopeSql,
                    deleteScopeParameters,
                    entities,
                    cancellationToken);

                if (deleteNotMatchedBySource && !BundlesDeleteInUpsert)
                {
                    await ExecuteDeleteNotMatchedAsync(
                        context,
                        connection,
                        tableName,
                        stagingTable,
                        matchColumns,
                        deleteScopeSql,
                        deleteScopeParameters,
                        config,
                        cancellationToken);
                }

                if (needsIdentitySync && !BundlesIdentityInUpsert)
                {
                    await SyncIdentitiesAsync(
                        context,
                        connection,
                        tableName,
                        stagingTable,
                        matchColumns,
                        identityColumns!,
                        entities,
                        config,
                        cancellationToken);
                }

                if (ownedTransaction != null)
                    await ownedTransaction.CommitAsync(cancellationToken);
            }
            finally
            {
                try
                {
                    await using var dropCmd = CreateCommand(connection, context, BuildDropStagingSql(stagingTable), config.TimeoutSeconds);
                    await dropCmd.ExecuteNonQueryAsync(cancellationToken);
                }
                catch
                {
                    // staging cleaned on session/connection end
                }
            }
        }
        catch (Exception ex) when (IsProviderException(ex))
        {
            if (ownedTransaction != null)
                await ownedTransaction.RollbackAsync(cancellationToken);
            throw WrapProviderException(ex, "upsert", typeof(T));
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
}
