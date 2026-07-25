using System.Linq.Expressions;
using ContextBulkExtension.Core.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace ContextBulkExtension.Core;

/// <summary>
/// Extension methods for DbContext to perform high-performance bulk operations.
/// </summary>
public static class DbContextBulkExtension
{
    /// <summary>
    /// Performs a high-performance bulk insert of entities.
    /// </summary>
    public static Task BulkInsertAsync<T>(this DbContext context, IList<T> entities, CancellationToken cancellationToken = default) where T : class
        => BulkInsertAsync(context, entities, new BulkConfig(), cancellationToken);

    /// <summary>
    /// Performs a high-performance bulk insert of entities with custom options.
    /// When <see cref="BulkConfig.IdentityOutput"/> is true, routes through staging upsert
    /// (InsertOnly) so generated identity values can be read back.
    /// </summary>
    public static async Task BulkInsertAsync<T>(this DbContext context, IList<T> entities, BulkConfig config, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(config);

        if (entities.Count == 0)
            return;

        // If identity output is requested, use BulkUpsert with InsertOnly mode
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
                InsertOnly = true
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

        var provider = BulkProviderRegistry.Resolve(context.Database.GetDbConnection());
        await provider.BulkInsertAsync(context, entities, config, cancellationToken);
    }

    /// <summary>
    /// Performs a high-performance bulk upsert (insert or update) of entities.
    /// </summary>
    public static Task BulkUpsertAsync<T>(
        this DbContext context,
        IList<T> entities,
        Expression<Func<T, object>>? matchOn = null,
        Expression<Func<T, object>>? updateColumns = null,
        BulkConfig? config = null,
        CancellationToken cancellationToken = default) where T : class
        => BulkUpsertInternalAsync(context, entities, matchOn, updateColumns, deleteScope: null, config, deleteNotMatchedBySource: false, cancellationToken);

    /// <summary>
    /// Performs a high-performance bulk upsert and deletes target rows not present in the source batch.
    /// </summary>
    public static Task BulkUpsertWithDeleteScopeAsync<T>(
        this DbContext context,
        IList<T> entities,
        Expression<Func<T, object>>? matchOn = null,
        Expression<Func<T, object>>? updateColumns = null,
        Expression<Func<T, bool>>? deleteScope = null,
        BulkConfig? config = null,
        CancellationToken cancellationToken = default) where T : class
        => BulkUpsertInternalAsync(context, entities, matchOn, updateColumns, deleteScope, config, deleteNotMatchedBySource: true, cancellationToken);

    private static async Task BulkUpsertInternalAsync<T>(
        DbContext context,
        IList<T> entities,
        Expression<Func<T, object>>? matchOn,
        Expression<Func<T, object>>? updateColumns,
        Expression<Func<T, bool>>? deleteScope,
        BulkConfig? config,
        bool deleteNotMatchedBySource,
        CancellationToken cancellationToken) where T : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entities);

        config ??= new BulkConfig();

        if (entities.Count == 0)
            return;

        var provider = BulkProviderRegistry.Resolve(context.Database.GetDbConnection());
        await provider.BulkUpsertAsync(
            context,
            entities,
            matchOn,
            updateColumns,
            deleteScope,
            config,
            deleteNotMatchedBySource,
            cancellationToken);
    }
}
