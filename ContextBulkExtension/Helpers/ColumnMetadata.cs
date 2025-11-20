using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ContextBulkExtension.Helpers;

/// <summary>
/// Metadata for a column mapping.
/// </summary>
internal class ColumnMetadata
{
    public required string ColumnName { get; init; }
    public required string SqlType { get; init; }
    public required System.Reflection.PropertyInfo PropertyInfo { get; init; }
    public required Type ClrType { get; init; }
	public required Type ProviderClrType { get; init; }
	public required string PropertyName { get; init; }
    public required Func<object, object?> CompiledGetter { get; init; }
    public required Action<object, object?> CompiledSetter { get; init; }
    public required bool IsIdentity { get; init; }
    public required bool IsPrimaryKey { get; init; }
	public ValueConverter? ValueConverter { get; init; }
}
