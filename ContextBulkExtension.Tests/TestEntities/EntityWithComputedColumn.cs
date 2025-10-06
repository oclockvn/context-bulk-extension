namespace ContextBulkExtension.Tests.TestEntities;

public class EntityWithComputedColumn
{
    public int Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty; // Computed column
    public DateTime UpdatedAt { get; set; } // Auto-updated on insert/update
}
