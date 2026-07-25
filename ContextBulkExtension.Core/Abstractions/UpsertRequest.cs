using System.Data.Common;
using ContextBulkExtension.Core.Helpers;
using Microsoft.EntityFrameworkCore;

namespace ContextBulkExtension.Core.Abstractions;

internal sealed class UpsertRequest<T> where T : class
{
    public required DbContext Context { get; init; }
    public required DbConnection Connection { get; init; }
    public required string TargetTable { get; init; }
    public required string StagingTable { get; init; }
    public required IReadOnlyList<ColumnMetadata> Columns { get; init; }
    public required IReadOnlyList<ColumnMetadata> MatchColumns { get; init; }
    public List<string>? UpdateColumnNames { get; init; }
    public required BulkConfig Config { get; init; }
    public IReadOnlyList<ColumnMetadata>? IdentityColumns { get; init; }
    public bool NeedsIdentitySync { get; init; }

    /// <summary>
    /// True when delete must run inside ExecuteUpsert (SqlServer MERGE bundle).
    /// </summary>
    public bool BundleDeleteNotMatched { get; init; }

    public string? DeleteScopeSql { get; init; }
    public List<DbParameter>? DeleteScopeParameters { get; init; }
    public required IList<T> Entities { get; init; }
}
