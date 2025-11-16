namespace ContextBulkExtension.Tests.TestEntities;

/// <summary>
/// Entity designed for testing deleteScope scenarios with multiple filter columns.
/// </summary>
public class MetricEntity
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public string Metric { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public decimal Value { get; set; }
    public string? Category { get; set; }
}
