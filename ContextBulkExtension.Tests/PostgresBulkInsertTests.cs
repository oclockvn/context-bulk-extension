using ContextBulkExtension.Core;
using ContextBulkExtension.Tests.Fixtures;
using ContextBulkExtension.Tests.TestEntities;
using Microsoft.EntityFrameworkCore;

namespace ContextBulkExtension.Tests;

[Collection("PostgresDatabase")]
public class PostgresBulkInsertTests(PostgresDatabaseFixture fixture) : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _fixture = fixture;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _fixture.ClearTableAsync<SimpleEntity>();
        await _fixture.ClearTableAsync<EntityWithoutIdentity>();
    }

    [Fact]
    public async Task BulkInsertAsync_WithMultipleEntities_ShouldInsertCorrectly()
    {
        var entities = Enumerable.Range(1, 50)
            .Select(i => new SimpleEntity
            {
                Name = $"Entity {i}",
                Value = i,
                CreatedAt = DateTime.UtcNow
            })
            .ToList();

        await using var context = _fixture.CreateNewContext();
        await context.BulkInsertAsync(entities);

        var count = await _fixture.GetCountAsync<SimpleEntity>();
        Assert.Equal(50, count);

        var insertedEntities = await _fixture.GetAllEntitiesAsync<SimpleEntity>();
        Assert.All(insertedEntities, e => Assert.True(e.Id > 0));

        var first = insertedEntities.First(e => e.Value == 1);
        Assert.Equal("Entity 1", first.Name);

        var last = insertedEntities.First(e => e.Value == 50);
        Assert.Equal("Entity 50", last.Name);
    }

    [Fact]
    public async Task BulkInsertAsync_WithinTransaction_ShouldCommitSuccessfully()
    {
        var entities = Enumerable.Range(1, 50)
            .Select(i => new SimpleEntity
            {
                Name = $"Transaction Entity {i}",
                Value = i,
                CreatedAt = DateTime.UtcNow
            })
            .ToList();

        await _fixture.ExecuteInTransactionAsync(async context =>
        {
            await context.BulkInsertAsync(entities);
        });

        var count = await _fixture.GetCountAsync<SimpleEntity>();
        Assert.Equal(50, count);
    }

    [Fact]
    public async Task BulkInsertAsync_WithinTransactionThatRollsBack_ShouldNotInsert()
    {
        var entities = Enumerable.Range(1, 50)
            .Select(i => new SimpleEntity
            {
                Name = $"Rollback Entity {i}",
                Value = i,
                CreatedAt = DateTime.UtcNow
            })
            .ToList();

        await using var context = _fixture.CreateNewContext();
        await using var transaction = await context.Database.BeginTransactionAsync();
        await context.BulkInsertAsync(entities);
        await transaction.RollbackAsync();

        var count = await _fixture.GetCountAsync<SimpleEntity>();
        Assert.Equal(0, count);
    }
}
