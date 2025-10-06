using ContextBulkExtension.Tests.Fixtures;
using ContextBulkExtension.Tests.TestEntities;
using Microsoft.EntityFrameworkCore;

namespace ContextBulkExtension.Tests;

[Collection("Database")]
public class BulkInsertTests : IAsyncLifetime
{
    private readonly DatabaseFixture _fixture;

    public BulkInsertTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

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
        await _fixture.Context.BulkInsertAsync(entities);
        var count = await _fixture.GetCountAsync<SimpleEntity>();
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task BulkInsertAsync_WithSingleEntity_ShouldInsertSuccessfully()
    {
        // Arrange
        var entity = new SimpleEntity
        {
            Name = "Test Entity",
            Value = 100,
            CreatedAt = DateTime.UtcNow
        };

        // Act
        await _fixture.Context.BulkInsertAsync(new[] { entity });

        // Assert
        var entities = await _fixture.GetAllEntitiesAsync<SimpleEntity>();
        Assert.Single(entities);
        Assert.Equal("Test Entity", entities[0].Name);
        Assert.Equal(100, entities[0].Value);
        Assert.True(entities[0].Id > 0);
    }

    [Fact]
    public async Task BulkInsertAsync_WithMultipleEntities_ShouldInsertAll()
    {
        // Arrange
        var entities = Enumerable.Range(1, 1000)
            .Select(i => new SimpleEntity
            {
                Name = $"Entity {i}",
                Value = i,
                CreatedAt = DateTime.UtcNow
            })
            .ToList();

        // Act
        await _fixture.Context.BulkInsertAsync(entities);

        // Assert
        var count = await _fixture.GetCountAsync<SimpleEntity>();
        Assert.Equal(1000, count);

        var insertedEntities = await _fixture.GetAllEntitiesAsync<SimpleEntity>();
        Assert.All(insertedEntities, e => Assert.True(e.Id > 0));
    }

    [Fact]
    public async Task BulkInsertAsync_WithKeepIdentityTrue_ShouldPreserveIdentityValues()
    {
        // Arrange
        var entities = new List<SimpleEntity>
        {
            new() { Id = 100, Name = "Entity 100", Value = 1, CreatedAt = DateTime.UtcNow },
            new() { Id = 200, Name = "Entity 200", Value = 2, CreatedAt = DateTime.UtcNow },
            new() { Id = 300, Name = "Entity 300", Value = 3, CreatedAt = DateTime.UtcNow }
        };

        var options = new BulkInsertOptions { KeepIdentity = true };

        // Act
        await _fixture.Context.BulkInsertAsync(entities, options);

        // Assert
        var insertedEntities = await _fixture.GetAllEntitiesAsync<SimpleEntity>();
        Assert.Equal(3, insertedEntities.Count);
        Assert.Contains(insertedEntities, e => e.Id == 100);
        Assert.Contains(insertedEntities, e => e.Id == 200);
        Assert.Contains(insertedEntities, e => e.Id == 300);
    }

    [Fact]
    public async Task BulkInsertAsync_WithKeepIdentityFalse_ShouldGenerateIdentityValues()
    {
        // Arrange
        var entities = new List<SimpleEntity>
        {
            new() { Id = 999, Name = "Entity 1", Value = 1, CreatedAt = DateTime.UtcNow },
            new() { Id = 998, Name = "Entity 2", Value = 2, CreatedAt = DateTime.UtcNow }
        };

        var options = new BulkInsertOptions { KeepIdentity = false };

        // Act
        await _fixture.Context.BulkInsertAsync(entities, options);

        // Assert
        var insertedEntities = await _fixture.GetAllEntitiesAsync<SimpleEntity>();
        Assert.Equal(2, insertedEntities.Count);
        Assert.DoesNotContain(insertedEntities, e => e.Id == 999);
        Assert.DoesNotContain(insertedEntities, e => e.Id == 998);
    }

    [Fact]
    public async Task BulkInsertAsync_WithCustomBatchSize_ShouldInsertAll()
    {
        // Arrange
        var entities = Enumerable.Range(1, 5000)
            .Select(i => new SimpleEntity
            {
                Name = $"Entity {i}",
                Value = i,
                CreatedAt = DateTime.UtcNow
            })
            .ToList();

        var options = new BulkInsertOptions { BatchSize = 500 };

        // Act
        await _fixture.Context.BulkInsertAsync(entities, options);

        // Assert
        var count = await _fixture.GetCountAsync<SimpleEntity>();
        Assert.Equal(5000, count);
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
        await _fixture.Context.BulkInsertAsync(entities);

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
        await _fixture.Context.BulkInsertAsync(entities);

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
        await _fixture.ExecuteInTransactionAsync(async () =>
        {
            await _fixture.Context.BulkInsertAsync(entities);
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
        await using var transaction = await _fixture.Context.Database.BeginTransactionAsync();
        await _fixture.Context.BulkInsertAsync(entities);
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
        List<SimpleEntity>? nullEntities = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _fixture.Context.BulkInsertAsync(nullEntities!));
    }

    [Fact]
    public async Task BulkInsertAsync_WithCheckConstraintsFalse_ShouldInsertSuccessfully()
    {
        // Arrange
        var entities = Enumerable.Range(1, 100)
            .Select(i => new SimpleEntity
            {
                Name = $"Entity {i}",
                Value = i,
                CreatedAt = DateTime.UtcNow
            })
            .ToList();

        var options = new BulkInsertOptions { CheckConstraints = false };

        // Act
        await _fixture.Context.BulkInsertAsync(entities, options);

        // Assert
        var count = await _fixture.GetCountAsync<SimpleEntity>();
        Assert.Equal(100, count);
    }

    [Fact]
    public async Task BulkInsertAsync_WithUseTableLockFalse_ShouldInsertSuccessfully()
    {
        // Arrange
        var entities = Enumerable.Range(1, 100)
            .Select(i => new SimpleEntity
            {
                Name = $"Entity {i}",
                Value = i,
                CreatedAt = DateTime.UtcNow
            })
            .ToList();

        var options = new BulkInsertOptions { UseTableLock = false };

        // Act
        await _fixture.Context.BulkInsertAsync(entities, options);

        // Assert
        var count = await _fixture.GetCountAsync<SimpleEntity>();
        Assert.Equal(100, count);
    }
}
