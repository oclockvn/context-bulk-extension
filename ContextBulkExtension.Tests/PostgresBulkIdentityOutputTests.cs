using ContextBulkExtension.Core;
using ContextBulkExtension.Tests.Fixtures;
using ContextBulkExtension.Tests.TestEntities;
using Microsoft.EntityFrameworkCore;

namespace ContextBulkExtension.Tests;

[Collection("PostgresDatabase")]
public class PostgresBulkIdentityOutputTests(PostgresDatabaseFixture fixture) : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _fixture = fixture;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _fixture.ClearTableAsync<UserEntity>();
        await _fixture.ClearTableAsync<SimpleEntity>();
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

    [Fact]
    public async Task BulkUpsertAsync_WithIdentityOutput_DefaultPk_ShouldSyncIds()
    {
        var entities = Enumerable.Range(1, 10)
            .Select(i => new SimpleEntity
            {
                Name = $"Entity {i}",
                Value = i,
                CreatedAt = DateTime.UtcNow
            })
            .ToList();

        await using var context = _fixture.CreateNewContext();
        await context.BulkUpsertAsync(entities, config: new BulkConfig { IdentityOutput = true });

        Assert.All(entities, e => Assert.True(e.Id > 0));
        Assert.Equal(10, entities.Select(e => e.Id).Distinct().Count());

        var all = await _fixture.GetAllEntitiesAsync<SimpleEntity>();
        foreach (var entity in entities)
            Assert.Contains(all, db => db.Id == entity.Id && db.Name == entity.Name);
    }

    [Fact]
    public async Task BulkInsertAsync_WithIdentityOutput_ShouldSyncIds()
    {
        var entities = Enumerable.Range(1, 5)
            .Select(i => new SimpleEntity
            {
                Name = $"Insert {i}",
                Value = i,
                CreatedAt = DateTime.UtcNow
            })
            .ToList();

        await using var context = _fixture.CreateNewContext();
        await context.BulkInsertAsync(entities, new BulkConfig { IdentityOutput = true });

        Assert.All(entities, e => Assert.True(e.Id > 0));
        Assert.Equal(5, entities.Select(e => e.Id).Distinct().Count());

        var all = await _fixture.GetAllEntitiesAsync<SimpleEntity>();
        Assert.Equal(5, all.Count);
        foreach (var entity in entities)
            Assert.Contains(all, db => db.Id == entity.Id && db.Name == entity.Name);
    }

    [Fact]
    public async Task BulkUpsertAsync_WithIdentityOutputAndInsertOnly_Existing_ShouldSyncId()
    {
        await _fixture.SeedDataAsync([
            new SimpleEntity { Name = "Keep", Value = 1, CreatedAt = DateTime.UtcNow }
        ]);
        var existingId = (await _fixture.GetAllEntitiesAsync<SimpleEntity>()).Single().Id;

        var entities = new List<SimpleEntity>
        {
            new() { Id = existingId, Name = "Keep", Value = 99, CreatedAt = DateTime.UtcNow }
        };

        await using var context = _fixture.CreateNewContext();
        await context.BulkUpsertAsync(
            entities,
            config: new BulkConfig { InsertOnly = true, IdentityOutput = true });

        Assert.Equal(existingId, entities[0].Id);
        Assert.Equal(1, (await _fixture.GetAllEntitiesAsync<SimpleEntity>()).Single().Value);
    }
}
