namespace ContextBulkExtension.Tests.TestEntities;

public class UserEntity
{
    public int Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public int Points { get; set; }
    public bool IsActive { get; set; }
    public DateTime RegisteredAt { get; set; }
}
