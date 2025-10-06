namespace ContextBulkExtension.Tests.TestEntities;

public class CompositeKeyEntity
{
    public int Key1 { get; set; }
    public string Key2 { get; set; } = string.Empty;
    public string Data { get; set; } = string.Empty;
    public int Counter { get; set; }
}
