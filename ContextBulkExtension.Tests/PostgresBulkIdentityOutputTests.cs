using ContextBulkExtension.Tests.Fixtures;
using ContextBulkExtension.Tests.TestEntities;

namespace ContextBulkExtension.Tests;

[Collection("PostgresDatabase")]
public class PostgresBulkIdentityOutputTests(PostgresDatabaseFixture fixture) : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _fixture = fixture;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _fixture.ClearTableAsync<UserEntity>();
    }

    [Fact]
    public async Task BulkUpsertAsync_WithIdentityOutput_ShouldSyncIds()
    {
        var entities = new List<UserEntity>
        {
            new()
            {
                Email = "new1@test.com",
                Username = "u1",
                FirstName = "N1",
                LastName = "L1",
                Points = 1,
                IsActive = true,
                RegisteredAt = DateTime.UtcNow
            },
            new()
            {
                Email = "new2@test.com",
                Username = "u2",
                FirstName = "N2",
                LastName = "L2",
                Points = 2,
                IsActive = true,
                RegisteredAt = DateTime.UtcNow
            }
        };

        await using var context = _fixture.CreateNewContext();
        await context.BulkUpsertAsync(
            entities,
            matchOn: x => x.Email,
            config: new BulkConfig { IdentityOutput = true });

        Assert.All(entities, e => Assert.True(e.Id > 0));
        Assert.NotEqual(entities[0].Id, entities[1].Id);

        var all = await _fixture.GetAllEntitiesAsync<UserEntity>();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, e => e.Id == entities[0].Id && e.Email == "new1@test.com");
        Assert.Contains(all, e => e.Id == entities[1].Id && e.Email == "new2@test.com");
    }

    [Fact]
    public async Task BulkUpsertAsync_WithIdentityOutput_Existing_ShouldSyncExistingId()
    {
        await _fixture.SeedDataAsync([
            new UserEntity
            {
                Email = "exist@test.com",
                Username = "ex",
                FirstName = "Old",
                LastName = "Name",
                Points = 1,
                IsActive = true,
                RegisteredAt = DateTime.UtcNow
            }
        ]);

        var existingId = (await _fixture.GetAllEntitiesAsync<UserEntity>()).Single().Id;

        var entities = new List<UserEntity>
        {
            new()
            {
                Email = "exist@test.com",
                Username = "ex",
                FirstName = "New",
                LastName = "Name",
                Points = 9,
                IsActive = true,
                RegisteredAt = DateTime.UtcNow
            }
        };

        await using var context = _fixture.CreateNewContext();
        await context.BulkUpsertAsync(
            entities,
            matchOn: x => x.Email,
            config: new BulkConfig { IdentityOutput = true });

        Assert.Equal(existingId, entities[0].Id);
        Assert.Equal("New", (await _fixture.GetAllEntitiesAsync<UserEntity>()).Single().FirstName);
    }
}
