using System.Collections;
using System.Data.Common;

namespace ContextBulkExtension;

/// <summary>
/// Memory-efficient IDataReader implementation for streaming entities to SqlBulkCopy.
/// Uses per-row value caching to avoid double property access (IsDBNull + typed getter).
/// </summary>
internal class EntityDataReader<T>(IList<T> entities, IReadOnlyList<ColumnMetadata> columns, bool includeRowIndex = false) : DbDataReader where T : class
{
    private readonly IList<T> _entities = entities;
    private readonly Dictionary<string, int> _ordinalCache = BuildOrdinalCache(columns, includeRowIndex);
    private int _currentRowIndex = -1;
    private bool _disposed;
    private object?[]? _currentRowValues;

    /// <summary>
    /// Builds a dictionary cache for O(1) column name to ordinal lookups.
    /// </summary>
    private static Dictionary<string, int> BuildOrdinalCache(IReadOnlyList<ColumnMetadata> columns, bool includeRowIndex)
    {
        var cache = new Dictionary<string, int>(
            includeRowIndex ? columns.Count + 1 : columns.Count,
            StringComparer.OrdinalIgnoreCase);

        if (includeRowIndex)
            cache[BulkOperationConstants.RowIndexColumnName] = 0;

        var startIndex = includeRowIndex ? 1 : 0;
        for (int i = 0; i < columns.Count; i++)
        {
            cache[columns[i].ColumnName] = i + startIndex;
        }

        return cache;
    }

    public override int FieldCount => includeRowIndex ? columns.Count + 1 : columns.Count;

    public override bool HasRows => true;

    public override bool IsClosed => _disposed;

    public override int RecordsAffected => -1;

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override int Depth => 0;

    public override bool Read()
    {
        _currentRowIndex++;
        _currentRowValues = null; // Clear cache for new row
        return _currentRowIndex < _entities.Count;
    }

    /// <summary>
    /// Ensures all column values for the current row are loaded into cache.
    /// This prevents double property access (IsDBNull + typed getter both calling GetValue).
    /// </summary>
    private void EnsureRowValuesLoaded()
    {
        if (_currentRowValues != null)
            return; // Already cached

        var entity = _entities[_currentRowIndex];
        var fieldCount = FieldCount;
        _currentRowValues = new object?[fieldCount];

        // If row index is included, set it as first value
        if (includeRowIndex)
        {
            _currentRowValues[0] = _currentRowIndex;
        }

        // Load all column values once
        var startIndex = includeRowIndex ? 1 : 0;
        for (int i = 0; i < columns.Count; i++)
        {
            var value = columns[i].CompiledGetter(entity);
            _currentRowValues[startIndex + i] = value ?? DBNull.Value; // Box only once
        }
    }

    public override object GetValue(int ordinal)
    {
        if (_currentRowIndex < 0 || _currentRowIndex >= _entities.Count)
            throw new InvalidOperationException("No current row");

        EnsureRowValuesLoaded();
        return _currentRowValues![ordinal]!;
    }

    public override string GetName(int ordinal)
    {
        if (includeRowIndex && ordinal == 0)
            return BulkOperationConstants.RowIndexColumnName;

        var columnIndex = includeRowIndex ? ordinal - 1 : ordinal;
        return columns[columnIndex].ColumnName;
    }

    public override int GetOrdinal(string name)
    {
        if (_ordinalCache.TryGetValue(name, out var ordinal))
            return ordinal;

        throw new IndexOutOfRangeException($"Column '{name}' not found");
    }

    public override string GetDataTypeName(int ordinal)
    {
        if (includeRowIndex && ordinal == 0)
            return "Int32";

        var columnIndex = includeRowIndex ? ordinal - 1 : ordinal;
        return columns[columnIndex].ClrType.Name;
    }

    public override Type GetFieldType(int ordinal)
    {
        if (includeRowIndex && ordinal == 0)
            return typeof(int);

        var columnIndex = includeRowIndex ? ordinal - 1 : ordinal;
        return columns[columnIndex].ClrType;
    }

    public override bool IsDBNull(int ordinal)
    {
        EnsureRowValuesLoaded();
        // Use pattern matching for more efficient null check
        // Note: Must check both null and DBNull to maintain semantics
        return _currentRowValues![ordinal] is null or DBNull;
    }

    public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);
    public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);
    public override char GetChar(int ordinal) => (char)GetValue(ordinal);
    public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);
    public override decimal GetDecimal(int ordinal) => (decimal)GetValue(ordinal);
    public override double GetDouble(int ordinal) => (double)GetValue(ordinal);
    public override float GetFloat(int ordinal) => (float)GetValue(ordinal);
    public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);
    public override short GetInt16(int ordinal) => (short)GetValue(ordinal);
    public override int GetInt32(int ordinal) => (int)GetValue(ordinal);
    public override long GetInt64(int ordinal) => (long)GetValue(ordinal);
    public override string GetString(int ordinal) => (string)GetValue(ordinal);

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        throw new NotImplementedException();
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        throw new NotImplementedException();
    }

    public override int GetValues(object[] values)
    {
        EnsureRowValuesLoaded();
        var count = Math.Min(values.Length, FieldCount);
        Array.Copy(_currentRowValues!, 0, values, 0, count);
        return count;
    }

    public override bool NextResult() => false;

    public override IEnumerator GetEnumerator()
    {
        throw new NotImplementedException();
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _currentRowValues = null;
            }
            _disposed = true;
        }
        base.Dispose(disposing);
    }
}
