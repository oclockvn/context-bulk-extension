namespace ContextBulkExtension.Tests.TestEntities;

public class EntityWithoutIdentity
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}
