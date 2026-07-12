using ContextBulkExtension.Tests.Fixtures;
using ContextBulkExtension.Tests.TestEntities;

namespace ContextBulkExtension.Tests;

[Collection("PostgresDatabase")]
public class PostgresBulkUpsertTests(PostgresDatabaseFixture fixture) : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _fixture = fixture;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _fixture.ClearTableAsync<SimpleEntity>();
        await _fixture.ClearTableAsync<UserEntity>();
    }

    [Fact]
    public async Task BulkUpsertAsync_WithBasicOperations_ShouldInsertUpdateAndMix()
    {
        await _fixture.SeedDataAsync([
            new SimpleEntity { Name = "Existing", Value = 1, CreatedAt = DateTime.UtcNow }
        ]);

        var existing = (await _fixture.GetAllEntitiesAsync<SimpleEntity>()).Single();

        var entities = new List<SimpleEntity>
        {
            new() { Id = existing.Id, Name = "Updated", Value = 10, CreatedAt = existing.CreatedAt },
            new() { Name = "New", Value = 2, CreatedAt = DateTime.UtcNow }
        };

        await using var context = _fixture.CreateNewContext();
        await context.BulkUpsertAsync(entities);

        var all = await _fixture.GetAllEntitiesAsync<SimpleEntity>();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, e => e.Name == "Updated" && e.Value == 10);
        Assert.Contains(all, e => e.Name == "New" && e.Value == 2);
    }

    [Fact]
    public async Task BulkUpsertAsync_WithInsertOnlyTrue_ShouldOnlyInsert()
    {
        await _fixture.SeedDataAsync([
            new SimpleEntity { Name = "Existing", Value = 1, CreatedAt = DateTime.UtcNow }
        ]);

        var existing = (await _fixture.GetAllEntitiesAsync<SimpleEntity>()).Single();

        var entities = new List<SimpleEntity>
        {
            new() { Id = existing.Id, Name = "ShouldNotUpdate", Value = 99, CreatedAt = existing.CreatedAt },
            new() { Name = "InsertedOnly", Value = 2, CreatedAt = DateTime.UtcNow }
        };

        await using var context = _fixture.CreateNewContext();
        await context.BulkUpsertAsync(entities, config: new BulkConfig { InsertOnly = true });

        var all = await _fixture.GetAllEntitiesAsync<SimpleEntity>();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, e => e.Id == existing.Id && e.Name == "Existing" && e.Value == 1);
        Assert.Contains(all, e => e.Name == "InsertedOnly");
    }

    [Fact]
    public async Task BulkUpsertAsync_WithCustomMatchOn_ShouldMatchOnEmail()
    {
        await _fixture.SeedDataAsync([
            new UserEntity
            {
                Email = "a@test.com",
                Username = "user_a",
                FirstName = "A",
                LastName = "One",
                Points = 1,
                IsActive = true,
                RegisteredAt = DateTime.UtcNow
            }
        ]);

        var entities = new List<UserEntity>
        {
            new()
            {
                Email = "a@test.com",
                Username = "user_a",
                FirstName = "Updated",
                LastName = "One",
                Points = 50,
                IsActive = true,
                RegisteredAt = DateTime.UtcNow
            },
            new()
            {
                Email = "b@test.com",
                Username = "user_b",
                FirstName = "B",
                LastName = "Two",
                Points = 2,
                IsActive = true,
                RegisteredAt = DateTime.UtcNow
            }
        };

        await using var context = _fixture.CreateNewContext();
        await context.BulkUpsertAsync(entities, matchOn: x => x.Email);

        var all = await _fixture.GetAllEntitiesAsync<UserEntity>();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, e => e.Email == "a@test.com" && e.FirstName == "Updated" && e.Points == 50);
        Assert.Contains(all, e => e.Email == "b@test.com" && e.FirstName == "B");
    }
}
