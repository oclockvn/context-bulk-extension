namespace ContextBulkExtension.Tests.TestEntities;

/// <summary>
/// Postgres-only entity covering date/time CLR types written via binary COPY.
/// </summary>
public class DateOnlyEntity
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public DateOnly? OptionalDate { get; set; }
    public TimeOnly Time { get; set; }
}
