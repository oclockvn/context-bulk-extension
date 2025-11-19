using ContextBulkExtension.Tests.Fixtures;
using ContextBulkExtension.Tests.TestEntities;
using Microsoft.EntityFrameworkCore;

namespace ContextBulkExtension.Tests;

public class BulkInsertTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private readonly DatabaseFixture _fixture = fixture;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Clean up tables after each test
        await _fixture.ClearTableAsync<SimpleEntity>();
        await _fixture.ClearTableAsync<EntityWithoutIdentity>();
        await _fixture.ClearTableAsync<EntityWithComputedColumn>();
    }

    [Fact]
    public async Task BulkInsertAsync_WithEmptyCollection_ShouldNotThrow()
    {
        // Arrange
        var entities = new List<SimpleEntity>();

        // Act & Assert
        await using var context = _fixture.CreateNewContext();
        await context.BulkInsertAsync(entities);
        var count = await _fixture.GetCountAsync<SimpleEntity>();
        Assert.Equal(0, count);
    }



    [Fact]
    public async Task BulkInsertAsync_WithMultipleEntities_ShouldInsertCorrectly()
    {
        // Arrange
        var entities = Enumerable.Range(1, 50)
            .Select(i => new SimpleEntity
            {
                Name = $"Entity {i}",
                Value = i,
                CreatedAt = DateTime.UtcNow
            })
            .ToList();

        // Act
        await using var context = _fixture.CreateNewContext();
        await context.BulkInsertAsync(entities);

        // Assert
        var count = await _fixture.GetCountAsync<SimpleEntity>();
        Assert.Equal(50, count);

        var insertedEntities = await _fixture.GetAllEntitiesAsync<SimpleEntity>();
        Assert.All(insertedEntities, e => Assert.True(e.Id > 0));

        // Verify data mapping
        var first = insertedEntities.First(e => e.Value == 1);
        Assert.Equal("Entity 1", first.Name);

        var last = insertedEntities.First(e => e.Value == 50);
        Assert.Equal("Entity 50", last.Name);
    }

    [Fact]
    public async Task BulkInsertAsync_WithCustomBatchSize_ShouldInsertAll()
    {
        // Arrange
        var entities = Enumerable.Range(1, 30)
            .Select(i => new SimpleEntity
            {
                Name = $"Entity {i}",
                Value = i,
                CreatedAt = DateTime.UtcNow
            })
            .ToList();

        var options = new BulkConfig { BatchSize = 10 };

        // Act
        await using var context = _fixture.CreateNewContext();
        await context.BulkInsertAsync(entities, options);

        // Assert
        var count = await _fixture.GetCountAsync<SimpleEntity>();
        Assert.Equal(30, count);
    }

    [Fact]
    public async Task BulkInsertAsync_WithEntityWithoutIdentity_ShouldInsertSuccessfully()
    {
        // Arrange
        var entities = Enumerable.Range(1, 100)
            .Select(i => new EntityWithoutIdentity
            {
                Id = Guid.NewGuid(),
                Code = $"CODE{i:D4}",
                Amount = i * 10.5m
            })
            .ToList();

        // Act
        await using var context = _fixture.CreateNewContext();
        await context.BulkInsertAsync(entities);

        // Assert
        var count = await _fixture.GetCountAsync<EntityWithoutIdentity>();
        Assert.Equal(100, count);
    }

    [Fact]
    public async Task BulkInsertAsync_WithComputedColumn_ShouldExcludeComputedColumn()
    {
        // Arrange
        var entities = new List<EntityWithComputedColumn>
        {
            new() { FirstName = "John", LastName = "Doe" },
            new() { FirstName = "Jane", LastName = "Smith" }
        };

        // Act
        await using var context = _fixture.CreateNewContext();
        await context.BulkInsertAsync(entities);

        // Assert
        var insertedEntities = await _fixture.GetAllEntitiesAsync<EntityWithComputedColumn>();
        Assert.Equal(2, insertedEntities.Count);

        // Verify computed column was calculated by SQL Server
        Assert.Equal("John Doe", insertedEntities.First(e => e.FirstName == "John").FullName);
        Assert.Equal("Jane Smith", insertedEntities.First(e => e.FirstName == "Jane").FullName);

        // Verify UpdatedAt was set by SQL Server
        Assert.All(insertedEntities, e => Assert.True(e.UpdatedAt > DateTime.MinValue));
    }

    [Fact]
    public async Task BulkInsertAsync_WithinTransaction_ShouldCommitSuccessfully()
    {
        // Arrange
        var entities = Enumerable.Range(1, 50)
            .Select(i => new SimpleEntity
            {
                Name = $"Transaction Entity {i}",
                Value = i,
                CreatedAt = DateTime.UtcNow
            })
            .ToList();

        // Act
        await _fixture.ExecuteInTransactionAsync(async (context) =>
        {
            await context.BulkInsertAsync(entities);
        });

        // Assert
        var count = await _fixture.GetCountAsync<SimpleEntity>();
        Assert.Equal(50, count);
    }

    [Fact]
    public async Task BulkInsertAsync_WithinTransactionThatRollsBack_ShouldNotInsert()
    {
        // Arrange
        var entities = Enumerable.Range(1, 50)
            .Select(i => new SimpleEntity
            {
                Name = $"Rollback Entity {i}",
                Value = i,
                CreatedAt = DateTime.UtcNow
            })
            .ToList();

        // Act
        await using var context = _fixture.CreateNewContext();
        await using var transaction = await context.Database.BeginTransactionAsync();
        await context.BulkInsertAsync(entities);
        await transaction.RollbackAsync();

        // Assert
        var count = await _fixture.GetCountAsync<SimpleEntity>();
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task BulkInsertAsync_WithNullContext_ShouldThrowArgumentNullException()
    {
        // Arrange
        TestDbContext? nullContext = null;
        var entities = new List<SimpleEntity> { new() { Name = "Test", Value = 1, CreatedAt = DateTime.UtcNow } };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            nullContext!.BulkInsertAsync(entities));
    }

    [Fact]
    public async Task BulkInsertAsync_WithNullEntities_ShouldThrowArgumentNullException()
    {
        // Arrange
        await using var context = _fixture.CreateNewContext();
        List<SimpleEntity>? nullEntities = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            context.BulkInsertAsync(nullEntities!));
    }

    [Fact]
    public async Task BulkInsertAsync_WithCheckConstraintsFalse_ShouldInsertSuccessfully()
    {
        // Arrange
        var entities = Enumerable.Range(1, 1)
            .Select(i => new SimpleEntity
            {
                Name = $"Entity {i}",
                Value = i,
                CreatedAt = DateTime.UtcNow
            })
            .ToList();

        var options = new BulkConfig { CheckConstraints = false };

        // Act
        await using var context = _fixture.CreateNewContext();
        await context.BulkInsertAsync(entities, options);

        // Assert
        var count = await _fixture.GetCountAsync<SimpleEntity>();
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task BulkInsertAsync_WithUseTableLockFalse_ShouldInsertSuccessfully()
    {
        // Arrange
        var entities = Enumerable.Range(1, 1)
            .Select(i => new SimpleEntity
            {
                Name = $"Entity {i}",
                Value = i,
                CreatedAt = DateTime.UtcNow
            })
            .ToList();

        var options = new BulkConfig { UseTableLock = false };

        // Act
        await using var context = _fixture.CreateNewContext();
        await context.BulkInsertAsync(entities, options);

        // Assert
        var count = await _fixture.GetCountAsync<SimpleEntity>();
        Assert.Equal(1, count);
    }
}
