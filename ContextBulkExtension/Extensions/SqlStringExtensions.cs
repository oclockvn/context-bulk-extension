namespace ContextBulkExtension.Extensions;

public static class SqlStringExtensions
{
    /// <summary>
    /// Escapes SQL Server identifiers by replacing ] with ]] and wrapping in brackets.
    /// </summary>
    public static string EscapeSqlIdentifier(this string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            throw new ArgumentException("SQL identifier cannot be null or empty.", nameof(identifier));

        // Replace ] with ]] (SQL Server escape sequence for brackets)
        return $"[{identifier.Replace("]", "]]")}]";
    }
}
