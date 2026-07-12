using System.Data.Common;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace ContextBulkExtension.Abstractions;

internal interface IBulkProvider
{
    bool Supports(DbConnection connection);

    Task BulkInsertAsync<T>(
        DbContext context,
        IList<T> entities,
        BulkConfig config,
        CancellationToken cancellationToken) where T : class;

    Task BulkUpsertAsync<T>(
        DbContext context,
        IList<T> entities,
        Expression<Func<T, object>>? matchOn,
        Expression<Func<T, object>>? updateColumns,
        Expression<Func<T, bool>>? deleteScope,
        BulkConfig config,
        bool deleteNotMatchedBySource,
        CancellationToken cancellationToken) where T : class;
}
