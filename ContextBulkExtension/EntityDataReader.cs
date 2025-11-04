using System.Collections;
using System.Data.Common;

namespace ContextBulkExtension;

/// <summary>
/// Memory-efficient IDataReader implementation for streaming entities to SqlBulkCopy.
/// </summary>
internal class EntityDataReader<T>(IEnumerable<T> entities, IReadOnlyList<ColumnMetadata> columns, bool includeRowIndex = false) : DbDataReader where T : class
{
    private readonly IEnumerator<T> _enumerator = entities.GetEnumerator();
    private bool _disposed;
    private int _currentRowIndex = -1;

    public override int FieldCount => includeRowIndex ? columns.Count + 1 : columns.Count;

    public override bool HasRows => true;

    public override bool IsClosed => _disposed;

    public override int RecordsAffected => -1;

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override int Depth => 0;

    public override bool Read()
    {
        var hasNext = _enumerator.MoveNext();
        if (hasNext)
        {
            _currentRowIndex++;
        }
        return hasNext;
    }

    public override object GetValue(int ordinal)
    {
        if (_enumerator.Current == null)
            throw new InvalidOperationException("No current row");

        // If row index is included, it's the first column (ordinal 0)
        if (includeRowIndex && ordinal == 0)
        {
            return _currentRowIndex;
        }

        // Adjust ordinal if row index is included
        var columnIndex = includeRowIndex ? ordinal - 1 : ordinal;
        var column = columns[columnIndex];
        var value = column.CompiledGetter(_enumerator.Current);

        return value ?? DBNull.Value;
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
        if (includeRowIndex && name.Equals(BulkOperationConstants.RowIndexColumnName, StringComparison.OrdinalIgnoreCase))
            return 0;

        var startIndex = includeRowIndex ? 1 : 0;
        for (int i = 0; i < columns.Count; i++)
        {
            if (columns[i].ColumnName.Equals(name, StringComparison.OrdinalIgnoreCase))
                return i + startIndex;
        }
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
        var value = GetValue(ordinal);
        return value == null || value == DBNull.Value;
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
        int count = Math.Min(values.Length, columns.Count);
        for (int i = 0; i < count; i++)
        {
            values[i] = GetValue(i);
        }
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
                _enumerator?.Dispose();
            }
            _disposed = true;
        }
        base.Dispose(disposing);
    }
}
