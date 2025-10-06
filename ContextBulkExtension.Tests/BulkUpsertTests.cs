using ContextBulkExtension.Tests.Fixtures;
using ContextBulkExtension.Tests.TestEntities;

namespace ContextBulkExtension.Tests;

[Collection("Database")]
public class BulkUpsertTests : IAsyncLifetime
{
    private readonly DatabaseFixture _fixture;

    public BulkUpsertTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Clean up tables after each test
        await _fixture.ClearTableAsync<SimpleEntity>();
        await _fixture.ClearTableAsync<CompositeKeyEntity>();
        await _fixture.ClearTableAsync<EntityWithoutIdentity>();
    }

    [Fact]
    public async Task BulkUpsertAsync_WithEmptyCollection_ShouldNotThrow()
    {
        // Arrange
        var entities = new List<SimpleEntity>();

        // Act & Assert
        await _fixture.Context.BulkUpsertAsync(entities);
        var count = await _fixture.GetCountAsync<SimpleEntity>();
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task BulkUpsertAsync_WithNewEntities_ShouldInsert()
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

        // Act
        await _fixture.Context.BulkUpsertAsync(entities);

        // Assert
        var count = await _fixture.GetCountAsync<SimpleEntity>();
        Assert.Equal(100, count);

        var insertedEntities = await _fixture.GetAllEntitiesAsync<SimpleEntity>();
        Assert.All(insertedEntities, e => Assert.True(e.Id > 0));
    }

    [Fact]
    public async Task BulkUpsertAsync_WithExistingEntities_ShouldUpdate()
    {
        // Arrange - Insert initial data
        var initialEntities = Enumerable.Range(1, 50)
            .Select(i => new SimpleEntity
            {
                Name = $"Initial {i}",
                Value = i,
                CreatedAt = DateTime.UtcNow
            })
            .ToList();

        await _fixture.Context.BulkInsertAsync(initialEntities);
        var insertedEntities = await _fixture.GetAllEntitiesAsync<SimpleEntity>();

        // Update the entities
        foreach (var entity in insertedEntities)
        {
            entity.Name = $"Updated {entity.Id}";
            entity.Value = entity.Value * 10;
        }

        // Act
        await _fixture.Context.BulkUpsertAsync(insertedEntities);

        // Assert
        var count = await _fixture.GetCountAsync<SimpleEntity>();
        Assert.Equal(50, count); // Same count, no duplicates

        var updatedEntities = await _fixture.GetAllEntitiesAsync<SimpleEntity>();
        Assert.All(updatedEntities, e => Assert.StartsWith("Updated", e.Name));
    }

    [Fact]
    public async Task BulkUpsertAsync_WithMixedNewAndExistingEntities_ShouldInsertAndUpdate()
    {
        // Arrange - Insert initial data
        var initialEntities = Enumerable.Range(1, 25)
            .Select(i => new SimpleEntity
            {
                Name = $"Initial {i}",
                Value = i,
                CreatedAt = DateTime.UtcNow
            })
            .ToList();

        await _fixture.Context.BulkInsertAsync(initialEntities);
        var existingEntities = await _fixture.GetAllEntitiesAsync<SimpleEntity>();

        // Modify existing and add new
        foreach (var entity in existingEntities)
        {
            entity.Value = entity.Value * 100;
        }

        var newEntities = Enumerable.Range(26, 25)
            .Select(i => new SimpleEntity
            {
                Name = $"New {i}",
                Value = i,
                CreatedAt = DateTime.UtcNow
            })
            .ToList();

        var mixedEntities = existingEntities.Concat(newEntities).ToList();

        // Act
        await _fixture.Context.BulkUpsertAsync(mixedEntities);

        // Assert
        var count = await _fixture.GetCountAsync<SimpleEntity>();
        Assert.Equal(50, count);

        var allEntities = await _fixture.GetAllEntitiesAsync<SimpleEntity>();
        var updated = allEntities.Where(e => e.Name.StartsWith("Initial")).ToList();
        var inserted = allEntities.Where(e => e.Name.StartsWith("New")).ToList();

        Assert.Equal(25, updated.Count);
        Assert.Equal(25, inserted.Count);
        Assert.All(updated, e => Assert.True(e.Value >= 100)); // Updated values
    }

    [Fact]
    public async Task BulkUpsertAsync_WithCompositeKey_ShouldUpsertCorrectly()
    {
        // Arrange - Insert initial data
        var initialEntities = new List<CompositeKeyEntity>
        {
            new() { Key1 = 1, Key2 = "A", Data = "Initial A", Counter = 1 },
            new() { Key1 = 2, Key2 = "B", Data = "Initial B", Counter = 2 },
            new() { Key1 = 3, Key2 = "C", Data = "Initial C", Counter = 3 }
        };

        await _fixture.Context.BulkInsertAsync(initialEntities);

        // Update existing and add new
        var upsertEntities = new List<CompositeKeyEntity>
        {
            new() { Key1 = 1, Key2 = "A", Data = "Updated A", Counter = 10 }, // Update
            new() { Key1 = 2, Key2 = "B", Data = "Updated B", Counter = 20 }, // Update
            new() { Key1 = 4, Key2 = "D", Data = "New D", Counter = 4 }       // Insert
        };

        // Act
        await _fixture.Context.BulkUpsertAsync(upsertEntities);

        // Assert
        var allEntities = await _fixture.GetAllEntitiesAsync<CompositeKeyEntity>();
        Assert.Equal(4, allEntities.Count);

        var entityA = allEntities.First(e => e.Key1 == 1 && e.Key2 == "A");
        Assert.Equal("Updated A", entityA.Data);
        Assert.Equal(10, entityA.Counter);

        var entityD = allEntities.First(e => e.Key1 == 4 && e.Key2 == "D");
        Assert.Equal("New D", entityD.Data);
    }

    [Fact]
    public async Task BulkUpsertAsync_WithInsertOnlyTrue_ShouldOnlyInsert()
    {
        // Arrange - Insert initial data
        var initialEntities = new List<SimpleEntity>
        {
            new() { Name = "Entity 1", Value = 1, CreatedAt = DateTime.UtcNow },
            new() { Name = "Entity 2", Value = 2, CreatedAt = DateTime.UtcNow }
        };

        await _fixture.Context.BulkInsertAsync(initialEntities);
        var existingEntities = await _fixture.GetAllEntitiesAsync<SimpleEntity>();

        // Try to update with InsertOnly = true
        foreach (var entity in existingEntities)
        {
            entity.Value = 999;
        }

        var options = new BulkUpsertOptions { InsertOnly = true };

        // Act
        await _fixture.Context.BulkUpsertAsync(existingEntities, options);

        // Assert - Values should not be updated
        var entities = await _fixture.GetAllEntitiesAsync<SimpleEntity>();
        Assert.Equal(2, entities.Count);
        Assert.DoesNotContain(entities, e => e.Value == 999);
    }

    [Fact]
    public async Task BulkUpsertAsync_WithUpdateColumns_ShouldOnlyUpdateSpecifiedColumns()
    {
        // Arrange - Insert initial data
        var initialEntity = new SimpleEntity
        {
            Name = "Original Name",
            Value = 100,
            CreatedAt = DateTime.UtcNow.AddDays(-1)
        };

        await _fixture.Context.BulkInsertAsync([initialEntity]);
        var existingEntity = (await _fixture.GetAllEntitiesAsync<SimpleEntity>()).First();

        // Modify entity
        existingEntity.Name = "Updated Name";
        existingEntity.Value = 999;
        existingEntity.CreatedAt = DateTime.UtcNow;

        var options = new BulkUpsertOptions
        {
            UpdateColumns = [nameof(SimpleEntity.Value)]
        };

        // Act
        await _fixture.Context.BulkUpsertAsync([existingEntity], options);

        // Assert
        var updatedEntity = (await _fixture.GetAllEntitiesAsync<SimpleEntity>()).First();
        Assert.Equal("Original Name", updatedEntity.Name); // Not updated
        Assert.Equal(999, updatedEntity.Value); // Updated
    }

    [Fact]
    public async Task BulkUpsertAsync_WithinTransaction_ShouldCommitSuccessfully()
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
            await _fixture.Context.BulkUpsertAsync(entities);
        });

        // Assert
        var count = await _fixture.GetCountAsync<SimpleEntity>();
        Assert.Equal(50, count);
    }

    [Fact]
    public async Task BulkUpsertAsync_WithinTransactionThatRollsBack_ShouldNotUpsert()
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
        await _fixture.Context.BulkUpsertAsync(entities);
        await transaction.RollbackAsync();

        // Assert
        var count = await _fixture.GetCountAsync<SimpleEntity>();
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task BulkUpsertAsync_WithEntityWithoutIdentity_ShouldUpsertSuccessfully()
    {
        // Arrange - Insert initial data
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        var initialEntities = new List<EntityWithoutIdentity>
        {
            new() { Id = id1, Code = "CODE001", Amount = 100m },
            new() { Id = id2, Code = "CODE002", Amount = 200m }
        };

        await _fixture.Context.BulkInsertAsync(initialEntities);

        // Update and add new
        var upsertEntities = new List<EntityWithoutIdentity>
        {
            new() { Id = id1, Code = "CODE001_UPDATED", Amount = 150m }, // Update
            new() { Id = Guid.NewGuid(), Code = "CODE003", Amount = 300m } // Insert
        };

        // Act
        await _fixture.Context.BulkUpsertAsync(upsertEntities);

        // Assert
        var count = await _fixture.GetCountAsync<EntityWithoutIdentity>();
        Assert.Equal(3, count);

        var updated = (await _fixture.GetAllEntitiesAsync<EntityWithoutIdentity>()).First(e => e.Id == id1);
        Assert.Equal("CODE001_UPDATED", updated.Code);
        Assert.Equal(150m, updated.Amount);
    }

    [Fact]
    public async Task BulkUpsertAsync_WithNullContext_ShouldThrowArgumentNullException()
    {
        // Arrange
        TestDbContext? nullContext = null;
        var entities = new List<SimpleEntity> { new() { Name = "Test", Value = 1, CreatedAt = DateTime.UtcNow } };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            nullContext!.BulkUpsertAsync(entities));
    }

    [Fact]
    public async Task BulkUpsertAsync_WithNullEntities_ShouldThrowArgumentNullException()
    {
        // Arrange
        List<SimpleEntity>? nullEntities = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _fixture.Context.BulkUpsertAsync(nullEntities!));
    }

    [Fact]
    public async Task BulkUpsertAsync_MultipleTimes_ShouldHandleCorrectly()
    {
        // Arrange & Act - First upsert (insert)
        var entities = new List<SimpleEntity>
        {
            new() { Name = "Entity 1", Value = 1, CreatedAt = DateTime.UtcNow },
            new() { Name = "Entity 2", Value = 2, CreatedAt = DateTime.UtcNow }
        };
        await _fixture.Context.BulkUpsertAsync(entities);

        // Get inserted entities with their IDs
        var firstUpsert = await _fixture.GetAllEntitiesAsync<SimpleEntity>();
        Assert.Equal(2, firstUpsert.Count);

        // Second upsert (update)
        foreach (var entity in firstUpsert)
        {
            entity.Value = entity.Value * 10;
        }
        await _fixture.Context.BulkUpsertAsync(firstUpsert);

        var secondUpsert = await _fixture.GetAllEntitiesAsync<SimpleEntity>();
        Assert.Equal(2, secondUpsert.Count);
        Assert.All(secondUpsert, e => Assert.True(e.Value >= 10));

        // Third upsert (mixed)
        firstUpsert[0].Value = 999;
        var newEntity = new SimpleEntity { Name = "Entity 3", Value = 3, CreatedAt = DateTime.UtcNow };
        var mixedEntities = firstUpsert.Take(1).Concat([newEntity]).ToList();

        await _fixture.Context.BulkUpsertAsync(mixedEntities);

        // Assert
        var finalEntities = await _fixture.GetAllEntitiesAsync<SimpleEntity>();
        Assert.Equal(3, finalEntities.Count);
        Assert.Contains(finalEntities, e => e.Value == 999);
    }

    [Fact]
    public async Task BulkUpsertAsync_WithLargeDataset_ShouldHandleEfficiently()
    {
        // Arrange - Insert 5000 entities
        var initialEntities = Enumerable.Range(1, 5000)
            .Select(i => new SimpleEntity
            {
                Name = $"Entity {i}",
                Value = i,
                CreatedAt = DateTime.UtcNow
            })
            .ToList();

        await _fixture.Context.BulkInsertAsync(initialEntities);
        var existingEntities = await _fixture.GetAllEntitiesAsync<SimpleEntity>();

        // Update half, keep half unchanged, add new
        var toUpdate = existingEntities.Take(2500).ToList();
        foreach (var entity in toUpdate)
        {
            entity.Value = entity.Value * 2;
        }

        var toKeep = existingEntities.Skip(2500).Take(2500).ToList();

        var toInsert = Enumerable.Range(5001, 2500)
            .Select(i => new SimpleEntity
            {
                Name = $"New Entity {i}",
                Value = i,
                CreatedAt = DateTime.UtcNow
            })
            .ToList();

        var allEntities = toUpdate.Concat(toKeep).Concat(toInsert).ToList();

        // Act
        await _fixture.Context.BulkUpsertAsync(allEntities);

        // Assert
        var count = await _fixture.GetCountAsync<SimpleEntity>();
        Assert.Equal(7500, count);
    }
}
