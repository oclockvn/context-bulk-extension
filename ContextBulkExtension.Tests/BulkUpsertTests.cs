using ContextBulkExtension.Tests.Fixtures;
using ContextBulkExtension.Tests.TestEntities;
using Microsoft.EntityFrameworkCore;

namespace ContextBulkExtension.Tests;

public class BulkUpsertTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private readonly DatabaseFixture _fixture = fixture;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Clean up tables after each test
        await _fixture.ClearTableAsync<SimpleEntity>();
        await _fixture.ClearTableAsync<CompositeKeyEntity>();
        await _fixture.ClearTableAsync<EntityWithoutIdentity>();
        await _fixture.ClearTableAsync<UserEntity>();
    }

    [Fact]
    public async Task BulkUpsertAsync_WithEmptyCollection_ShouldNotThrow()
    {
        // Arrange
        var entities = new List<SimpleEntity>();

        // Act & Assert
        await using var context = _fixture.CreateNewContext();
        await context.BulkUpsertAsync(entities);
        var count = await _fixture.GetCountAsync<SimpleEntity>();
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task BulkUpsertAsync_WithBasicOperations_ShouldInsertUpdateAndMix()
    {
        // Test 1: Insert new entities
        var newEntities = Enumerable.Range(1, 50)
            .Select(i => new SimpleEntity
            {
                Name = $"Entity {i}",
                Value = i,
                CreatedAt = DateTime.UtcNow
            })
            .ToList();

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(newEntities);
        }

        var count = await _fixture.GetCountAsync<SimpleEntity>();
        Assert.Equal(50, count);
        var insertedEntities = await _fixture.GetAllEntitiesAsync<SimpleEntity>();
        Assert.All(insertedEntities, e => Assert.True(e.Id > 0));

        // Test 2: Update existing entities
        foreach (var entity in insertedEntities)
        {
            entity.Name = $"Updated {entity.Id}";
            entity.Value = entity.Value * 10;
        }

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(insertedEntities);
        }

        count = await _fixture.GetCountAsync<SimpleEntity>();
        Assert.Equal(50, count); // Same count, no duplicates
        var updatedEntities = await _fixture.GetAllEntitiesAsync<SimpleEntity>();
        Assert.All(updatedEntities, e => Assert.StartsWith("Updated", e.Name));

        // Test 3: Mixed new and existing entities
        var existingEntities = await _fixture.GetAllEntitiesAsync<SimpleEntity>();
        foreach (var entity in existingEntities)
        {
            entity.Value = entity.Value * 100;
        }

        var additionalEntities = Enumerable.Range(51, 25)
            .Select(i => new SimpleEntity
            {
                Name = $"New {i}",
                Value = i,
                CreatedAt = DateTime.UtcNow
            })
            .ToList();

        var mixedEntities = existingEntities.Concat(additionalEntities).ToList();

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(mixedEntities);
        }

        count = await _fixture.GetCountAsync<SimpleEntity>();
        Assert.Equal(75, count);

        var allEntities = await _fixture.GetAllEntitiesAsync<SimpleEntity>();
        var updated = allEntities.Where(e => e.Name.StartsWith("Updated")).ToList();
        var inserted = allEntities.Where(e => e.Name.StartsWith("New")).ToList();

        Assert.Equal(50, updated.Count);
        Assert.Equal(25, inserted.Count);
        Assert.All(updated, e => Assert.True(e.Value >= 1000)); // Updated values (10 * 100)
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

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkInsertAsync(initialEntities);
        }

        // Update existing and add new
        var upsertEntities = new List<CompositeKeyEntity>
        {
            new() { Key1 = 1, Key2 = "A", Data = "Updated A", Counter = 10 }, // Update
            new() { Key1 = 2, Key2 = "B", Data = "Updated B", Counter = 20 }, // Update
            new() { Key1 = 4, Key2 = "D", Data = "New D", Counter = 4 }       // Insert
        };

        // Act
        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(upsertEntities);
        }

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

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkInsertAsync(initialEntities);
        }

        // Get fresh context and load entities
        List<SimpleEntity> existingEntities;
        await using (var context1 = _fixture.CreateNewContext())
        {
            existingEntities = await context1.Set<SimpleEntity>().AsNoTracking().ToListAsync();
        }

        // Try to update with InsertOnly = true
        foreach (var entity in existingEntities)
        {
            entity.Value = 999;
        }

        var options = new BulkConfig { InsertOnly = true };

        // Act - Use fresh context for upsert
        await using (var context2 = _fixture.CreateNewContext())
        {
            await context2.BulkUpsertAsync(existingEntities, options: options);
        }

        // Assert - Use fresh context to verify
        await using (var context3 = _fixture.CreateNewContext())
        {
            var entities = await context3.Set<SimpleEntity>().AsNoTracking().ToListAsync();
            Assert.Equal(2, entities.Count);
            Assert.DoesNotContain(entities, e => e.Value == 999);
        }
    }


    [Fact]
    public async Task BulkUpsertAsync_WithinTransaction_ShouldCommitAndRollback()
    {
        // Test 1: Commit transaction
        var commitEntities = Enumerable.Range(1, 50)
            .Select(i => new SimpleEntity
            {
                Name = $"Transaction Entity {i}",
                Value = i,
                CreatedAt = DateTime.UtcNow
            })
            .ToList();

        await _fixture.ExecuteInTransactionAsync(async (context) =>
        {
            await context.BulkUpsertAsync(commitEntities);
        });

        var count = await _fixture.GetCountAsync<SimpleEntity>();
        Assert.Equal(50, count);

        // Clean up for rollback test
        await _fixture.ClearTableAsync<SimpleEntity>();

        // Test 2: Rollback transaction
        var rollbackEntities = Enumerable.Range(1, 50)
            .Select(i => new SimpleEntity
            {
                Name = $"Rollback Entity {i}",
                Value = i,
                CreatedAt = DateTime.UtcNow
            })
            .ToList();

        await using var context = _fixture.CreateNewContext();
        await using var transaction = await context.Database.BeginTransactionAsync();
        await context.BulkUpsertAsync(rollbackEntities);
        await transaction.RollbackAsync();

        count = await _fixture.GetCountAsync<SimpleEntity>();
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

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkInsertAsync(initialEntities);
        }

        // Update and add new
        var upsertEntities = new List<EntityWithoutIdentity>
        {
            new() { Id = id1, Code = "CODE001_UPDATED", Amount = 150m }, // Update
            new() { Id = Guid.NewGuid(), Code = "CODE003", Amount = 300m } // Insert
        };

        // Act
        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(upsertEntities);
        }

        // Assert
        var count = await _fixture.GetCountAsync<EntityWithoutIdentity>();
        Assert.Equal(3, count);

        var updated = (await _fixture.GetAllEntitiesAsync<EntityWithoutIdentity>()).First(e => e.Id == id1);
        Assert.Equal("CODE001_UPDATED", updated.Code);
        Assert.Equal(150m, updated.Amount);
    }

    [Fact]
    public async Task BulkUpsertAsync_WithNullArguments_ShouldThrowArgumentNullException()
    {
        // Test 1: Null context
        TestDbContext? nullContext = null;
        var entities = new List<SimpleEntity> { new() { Name = "Test", Value = 1, CreatedAt = DateTime.UtcNow } };

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            nullContext!.BulkUpsertAsync(entities));

        // Test 2: Null entities
        await using var context = _fixture.CreateNewContext();
        List<SimpleEntity>? nullEntities = null;

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            context.BulkUpsertAsync(nullEntities!));
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

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkInsertAsync(initialEntities);
        }

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
        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(allEntities);
        }

        // Assert
        var count = await _fixture.GetCountAsync<SimpleEntity>();
        Assert.Equal(7500, count);
    }

    #region Custom MatchOn Tests

    [Fact]
    public async Task BulkUpsertAsync_WithCustomMatchOn_ShouldHandleSingleCompositeAndMixed()
    {
        // Test 1: Single column match (Email)
        var initialUsers1 = new List<UserEntity>
        {
            new() { Email = "user1@test.com", Username = "user1", FirstName = "John", LastName = "Doe", Points = 100, IsActive = true, RegisteredAt = DateTime.UtcNow },
            new() { Email = "user2@test.com", Username = "user2", FirstName = "Jane", LastName = "Smith", Points = 200, IsActive = true, RegisteredAt = DateTime.UtcNow }
        };

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkInsertAsync(initialUsers1);
        }

        var existingUsers1 = await _fixture.GetAllEntitiesAsync<UserEntity>();
        var updatedUsers1 = new List<UserEntity>
        {
            new() { Email = "user1@test.com", Username = "updated_user1", FirstName = "Johnny", LastName = "Updated", Points = 999, IsActive = false, RegisteredAt = DateTime.UtcNow },
            new() { Email = "user2@test.com", Username = "updated_user2", FirstName = "Janet", LastName = "Updated", Points = 888, IsActive = false, RegisteredAt = DateTime.UtcNow },
            new() { Email = "user3@test.com", Username = "user3", FirstName = "New", LastName = "User", Points = 300, IsActive = true, RegisteredAt = DateTime.UtcNow }
        };

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(updatedUsers1, matchOn: x => x.Email);
        }

        var allUsers1 = await _fixture.GetAllEntitiesAsync<UserEntity>();
        Assert.Equal(3, allUsers1.Count);
        var user1 = allUsers1.First(u => u.Email == "user1@test.com");
        Assert.Equal("updated_user1", user1.Username);
        Assert.Equal(existingUsers1[0].Id, user1.Id); // Same ID (updated, not inserted)
        var user3 = allUsers1.First(u => u.Email == "user3@test.com");
        Assert.True(user3.Id > 0); // Has new ID

        // Clean up for next test
        await _fixture.ClearTableAsync<UserEntity>();

        // Test 2: Composite match (Email + Username)
        var initialUsers2 = new List<UserEntity>
        {
            new() { Email = "user1@test.com", Username = "john_doe", FirstName = "John", LastName = "Doe", Points = 100, IsActive = true, RegisteredAt = DateTime.UtcNow },
            new() { Email = "user2@test.com", Username = "jane_smith", FirstName = "Jane", LastName = "Smith", Points = 200, IsActive = true, RegisteredAt = DateTime.UtcNow }
        };

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkInsertAsync(initialUsers2);
        }

        var updatedUsers2 = new List<UserEntity>
        {
            new() { Email = "user1@test.com", Username = "john_doe", FirstName = "Johnny", LastName = "Updated", Points = 999, IsActive = false, RegisteredAt = DateTime.UtcNow },
            new() { Email = "user3@test.com", Username = "new_user", FirstName = "New", LastName = "User", Points = 300, IsActive = true, RegisteredAt = DateTime.UtcNow }
        };

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(updatedUsers2, matchOn: x => new { x.Email, x.Username });
        }

        var allUsers2 = await _fixture.GetAllEntitiesAsync<UserEntity>();
        Assert.Equal(3, allUsers2.Count);
        var updatedUser = allUsers2.First(u => u.Email == "user1@test.com" && u.Username == "john_doe");
        Assert.Equal("Johnny", updatedUser.FirstName);

        // Clean up for next test
        await _fixture.ClearTableAsync<UserEntity>();

        // Test 3: Mixed insert/update with single column match
        var initialUsers3 = new List<UserEntity>
        {
            new() { Email = "existing1@test.com", Username = "user1", FirstName = "John", LastName = "Doe", Points = 100, IsActive = true, RegisteredAt = DateTime.UtcNow },
            new() { Email = "existing2@test.com", Username = "user2", FirstName = "Jane", LastName = "Smith", Points = 200, IsActive = true, RegisteredAt = DateTime.UtcNow }
        };

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkInsertAsync(initialUsers3);
        }

        var mixedUsers = new List<UserEntity>
        {
            new() { Email = "existing1@test.com", Username = "updated", FirstName = "Johnny", LastName = "Updated", Points = 999, IsActive = false, RegisteredAt = DateTime.UtcNow },
            new() { Email = "new1@test.com", Username = "new1", FirstName = "New1", LastName = "User1", Points = 300, IsActive = true, RegisteredAt = DateTime.UtcNow },
            new() { Email = "existing2@test.com", Username = "updated2", FirstName = "Janet", LastName = "Updated2", Points = 888, IsActive = false, RegisteredAt = DateTime.UtcNow },
            new() { Email = "new2@test.com", Username = "new2", FirstName = "New2", LastName = "User2", Points = 400, IsActive = true, RegisteredAt = DateTime.UtcNow }
        };

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(mixedUsers, matchOn: x => x.Email);
        }

        var allUsers3 = await _fixture.GetAllEntitiesAsync<UserEntity>();
        Assert.Equal(4, allUsers3.Count);
        var updatedUsers = allUsers3.Where(u => u.Email.StartsWith("existing")).ToList();
        Assert.Equal(2, updatedUsers.Count);
        Assert.All(updatedUsers, u => Assert.False(u.IsActive));
        var insertedUsers = allUsers3.Where(u => u.Email.StartsWith("new")).ToList();
        Assert.Equal(2, insertedUsers.Count);
        Assert.All(insertedUsers, u => Assert.True(u.IsActive));
    }

    [Fact]
    public async Task BulkUpsertAsync_WithCustomMatchOnAndInsertOnly_ShouldOnlyInsertNew()
    {
        // Arrange - Insert initial users
        var initialUsers = new List<UserEntity>
        {
            new() { Email = "user1@test.com", Username = "user1", FirstName = "John", LastName = "Doe", Points = 100, IsActive = true, RegisteredAt = DateTime.UtcNow }
        };

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkInsertAsync(initialUsers);
        }

        // Try to update existing and insert new
        var upsertUsers = new List<UserEntity>
        {
            new() { Email = "user1@test.com", Username = "updated", FirstName = "Updated", LastName = "Name", Points = 999, IsActive = false, RegisteredAt = DateTime.UtcNow }, // Should be ignored
            new() { Email = "user2@test.com", Username = "user2", FirstName = "New", LastName = "User", Points = 200, IsActive = true, RegisteredAt = DateTime.UtcNow } // Should be inserted
        };

        var options = new BulkConfig { InsertOnly = true };

        // Act - Match on Email with InsertOnly
        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(upsertUsers, matchOn: x => x.Email, options: options);
        }

        // Assert
        var allUsers = await _fixture.GetAllEntitiesAsync<UserEntity>();
        Assert.Equal(2, allUsers.Count);

        var user1 = allUsers.First(u => u.Email == "user1@test.com");
        Assert.Equal("John", user1.FirstName); // NOT updated
        Assert.Equal(100, user1.Points); // NOT updated
        Assert.True(user1.IsActive); // NOT updated

        var user2 = allUsers.First(u => u.Email == "user2@test.com");
        Assert.Equal("New", user2.FirstName); // Inserted
    }

    #endregion

    #region UpdateColumns Tests

    [Fact]
    public async Task BulkUpsertAsync_WithUpdateColumns_ShouldOnlyUpdateSpecifiedColumns()
    {
        // Test 1: Single column update (SimpleEntity)
        var initialEntity = new SimpleEntity
        {
            Name = "Original Name",
            Value = 100,
            CreatedAt = DateTime.UtcNow.AddDays(-1)
        };

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkInsertAsync([initialEntity]);
        }

        var existingEntity = (await _fixture.GetAllEntitiesAsync<SimpleEntity>()).First();
        existingEntity.Name = "Updated Name";
        existingEntity.Value = 999;
        existingEntity.CreatedAt = DateTime.UtcNow;

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync([existingEntity], updateColumns: x => x.Value);
        }

        var updatedEntity = (await _fixture.GetAllEntitiesAsync<SimpleEntity>()).First();
        Assert.Equal("Original Name", updatedEntity.Name); // Not updated
        Assert.Equal(999, updatedEntity.Value); // Updated

        // Clean up for next test
        await _fixture.ClearTableAsync<SimpleEntity>();

        // Test 2: Multiple columns update (UserEntity)
        var initialUsers = new List<UserEntity>
        {
            new() { Email = "user1@test.com", Username = "user1", FirstName = "John", LastName = "Doe", Points = 100, IsActive = true, RegisteredAt = DateTime.UtcNow.AddDays(-7) },
            new() { Email = "user2@test.com", Username = "user2", FirstName = "Jane", LastName = "Smith", Points = 200, IsActive = true, RegisteredAt = DateTime.UtcNow.AddDays(-7) }
        };

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkInsertAsync(initialUsers);
        }

        var existingUsers = await _fixture.GetAllEntitiesAsync<UserEntity>();
        foreach (var user in existingUsers)
        {
            user.Username = "updated_username";
            user.FirstName = "UpdatedFirst";
            user.LastName = "UpdatedLast";
            user.Points = 999;
            user.IsActive = false;
            user.RegisteredAt = DateTime.UtcNow;
        }

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(existingUsers, updateColumns: x => new { x.Points, x.IsActive });
        }

        var updatedUsers = await _fixture.GetAllEntitiesAsync<UserEntity>();
        Assert.Equal(2, updatedUsers.Count);
        foreach (var user in updatedUsers)
        {
            Assert.Equal(999, user.Points);
            Assert.False(user.IsActive);
            Assert.NotEqual("updated_username", user.Username);
            Assert.NotEqual("UpdatedFirst", user.FirstName);
            Assert.NotEqual("UpdatedLast", user.LastName);
        }

        // Clean up for next test
        await _fixture.ClearTableAsync<UserEntity>();

        // Test 3: Single column update on composite key entity
        var initialEntities = new List<CompositeKeyEntity>
        {
            new() { Key1 = 1, Key2 = "A", Data = "Initial Data A", Counter = 100 },
            new() { Key1 = 2, Key2 = "B", Data = "Initial Data B", Counter = 200 }
        };

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkInsertAsync(initialEntities);
        }

        var updatedEntities = new List<CompositeKeyEntity>
        {
            new() { Key1 = 1, Key2 = "A", Data = "Updated Data A", Counter = 999 },
            new() { Key1 = 2, Key2 = "B", Data = "Updated Data B", Counter = 888 }
        };

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(updatedEntities, updateColumns: x => x.Counter);
        }

        var allEntities = await _fixture.GetAllEntitiesAsync<CompositeKeyEntity>();
        Assert.Equal(2, allEntities.Count);
        var entityA = allEntities.First(e => e.Key1 == 1 && e.Key2 == "A");
        Assert.Equal("Initial Data A", entityA.Data); // NOT updated
        Assert.Equal(999, entityA.Counter); // Updated
        var entityB = allEntities.First(e => e.Key1 == 2 && e.Key2 == "B");
        Assert.Equal("Initial Data B", entityB.Data); // NOT updated
        Assert.Equal(888, entityB.Counter); // Updated
    }

    #endregion

    #region Combined MatchOn + UpdateColumns Tests

    [Fact]
    public async Task BulkUpsertAsync_WithMatchOnAndUpdateColumns_ShouldWorkTogether()
    {
        // Test 1: Single column matchOn with multiple updateColumns
        var initialUsers1 = new List<UserEntity>
        {
            new() { Email = "user1@test.com", Username = "user1", FirstName = "John", LastName = "Doe", Points = 100, IsActive = true, RegisteredAt = DateTime.UtcNow.AddDays(-7) },
            new() { Email = "user2@test.com", Username = "user2", FirstName = "Jane", LastName = "Smith", Points = 200, IsActive = true, RegisteredAt = DateTime.UtcNow.AddDays(-7) }
        };

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkInsertAsync(initialUsers1);
        }

        var updatedUsers1 = new List<UserEntity>
        {
            new() { Email = "user1@test.com", Username = "updated_user1", FirstName = "Johnny", LastName = "Updated", Points = 999, IsActive = false, RegisteredAt = DateTime.UtcNow },
            new() { Email = "user2@test.com", Username = "updated_user2", FirstName = "Janet", LastName = "Updated", Points = 888, IsActive = false, RegisteredAt = DateTime.UtcNow },
            new() { Email = "user3@test.com", Username = "user3", FirstName = "New", LastName = "User", Points = 300, IsActive = true, RegisteredAt = DateTime.UtcNow }
        };

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(updatedUsers1, matchOn: x => x.Email, updateColumns: x => new { x.FirstName, x.Points });
        }

        var allUsers1 = await _fixture.GetAllEntitiesAsync<UserEntity>();
        Assert.Equal(3, allUsers1.Count);
        var user1 = allUsers1.First(u => u.Email == "user1@test.com");
        Assert.Equal("Johnny", user1.FirstName);
        Assert.Equal(999, user1.Points);
        Assert.Equal("user1", user1.Username); // Original value
        Assert.Equal("Doe", user1.LastName); // Original value
        Assert.True(user1.IsActive); // Original value
        var user3 = allUsers1.First(u => u.Email == "user3@test.com");
        Assert.Equal("New", user3.FirstName);
        Assert.Equal(300, user3.Points);

        // Clean up for next test
        await _fixture.ClearTableAsync<UserEntity>();

        // Test 2: Composite matchOn with single updateColumn
        var initialUsers2 = new List<UserEntity>
        {
            new() { Email = "user1@test.com", Username = "john_doe", FirstName = "John", LastName = "Doe", Points = 100, IsActive = true, RegisteredAt = DateTime.UtcNow.AddDays(-7) }
        };

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkInsertAsync(initialUsers2);
        }

        var updatedUsers2 = new List<UserEntity>
        {
            new() { Email = "user1@test.com", Username = "john_doe", FirstName = "Johnny", LastName = "Updated", Points = 999, IsActive = false, RegisteredAt = DateTime.UtcNow },
            new() { Email = "user2@test.com", Username = "jane_smith", FirstName = "Jane", LastName = "Smith", Points = 200, IsActive = true, RegisteredAt = DateTime.UtcNow }
        };

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(updatedUsers2, matchOn: x => new { x.Email, x.Username }, updateColumns: x => x.LastName);
        }

        var allUsers2 = await _fixture.GetAllEntitiesAsync<UserEntity>();
        Assert.Equal(2, allUsers2.Count);
        var user1_2 = allUsers2.First(u => u.Email == "user1@test.com");
        Assert.Equal("Updated", user1_2.LastName); // Updated
        Assert.Equal("John", user1_2.FirstName); // NOT updated
        Assert.Equal(100, user1_2.Points); // NOT updated
        Assert.True(user1_2.IsActive); // NOT updated
    }

    #endregion


    #region IdentityOutput Tests

    [Fact]
    public async Task BulkUpsertAsync_WithIdentityOutput_ShouldSyncOrNotBasedOnSetting()
    {
        // Test 1: IdentityOutput enabled
        var entities1 = Enumerable.Range(1, 50)
            .Select(i => new SimpleEntity
            {
                Name = $"Entity {i}",
                Value = i,
                CreatedAt = DateTime.UtcNow
            })
            .ToList();

        var options1 = new BulkConfig { IdentityOutput = true };

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(entities1, options: options1);
        }

        Assert.All(entities1, e => Assert.True(e.Id > 0, "Entity ID should be synced from database"));
        var uniqueIds = entities1.Select(e => e.Id).Distinct().Count();
        Assert.Equal(50, uniqueIds);
        var dbEntities1 = await _fixture.GetAllEntitiesAsync<SimpleEntity>();
        foreach (var entity in entities1)
        {
            Assert.Contains(dbEntities1, db => db.Id == entity.Id && db.Name == entity.Name);
        }

        // Clean up for next test
        await _fixture.ClearTableAsync<SimpleEntity>();

        // Test 2: IdentityOutput disabled
        var entities2 = Enumerable.Range(1, 50)
            .Select(i => new SimpleEntity
            {
                Name = $"Entity {i}",
                Value = i,
                CreatedAt = DateTime.UtcNow
            })
            .ToList();

        var options2 = new BulkConfig { IdentityOutput = false };

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(entities2, options: options2);
        }

        Assert.All(entities2, e => Assert.Equal(0, e.Id));
        var count = await _fixture.GetCountAsync<SimpleEntity>();
        Assert.Equal(50, count);
    }

    [Fact]
    public async Task BulkUpsertAsync_WithIdentityOutputOnMixedOperations_ShouldSyncIdsCorrectly()
    {
        // Ensure clean state at start
        await _fixture.ClearTableAsync<SimpleEntity>();
        await _fixture.ClearTableAsync<UserEntity>();

        // Test 1: Mixed insert/update with primary key (SimpleEntity)
        var initialEntities = Enumerable.Range(1, 25)
            .Select(i => new SimpleEntity
            {
                Name = $"Initial {i}",
                Value = i,
                CreatedAt = DateTime.UtcNow
            })
            .ToList();

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkInsertAsync(initialEntities);
        }

        var existingEntities = await _fixture.GetAllEntitiesAsync<SimpleEntity>();
        var originalIds = existingEntities.Select(e => e.Id).ToList();

        foreach (var entity in existingEntities)
        {
            entity.Value = entity.Value * 10;
        }

        var newEntities = Enumerable.Range(26, 25)
            .Select(i => new SimpleEntity
            {
                Name = $"New {i}",
                Value = i,
                CreatedAt = DateTime.UtcNow
            })
            .ToList();

        var allEntities = existingEntities.Concat(newEntities).ToList();
        var options = new BulkConfig { IdentityOutput = true };

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(allEntities, options: options);
        }

        for (int i = 0; i < existingEntities.Count; i++)
        {
            Assert.Equal(originalIds[i], existingEntities[i].Id);
        }
        Assert.All(newEntities, e => Assert.True(e.Id > 0, "New entity ID should be synced"));
        Assert.All(newEntities, e => Assert.DoesNotContain(originalIds, id => id == e.Id));

        // Clean up for next test
        await _fixture.ClearTableAsync<SimpleEntity>();
        await _fixture.ClearTableAsync<UserEntity>();

        // Test 2: Mixed insert/update with custom matchOn (UserEntity)
        var initialUsers = new List<UserEntity>
        {
            new() { Email = "user1@test.com", Username = "user1", FirstName = "John", LastName = "Doe", Points = 100, IsActive = true, RegisteredAt = DateTime.UtcNow },
            new() { Email = "user2@test.com", Username = "user2", FirstName = "Jane", LastName = "Smith", Points = 200, IsActive = true, RegisteredAt = DateTime.UtcNow }
        };

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkInsertAsync(initialUsers);
        }

        var dbUsers = await _fixture.GetAllEntitiesAsync<UserEntity>();
        var existingUserId = dbUsers.First(u => u.Email == "user1@test.com").Id;

        var mixedUsers = new List<UserEntity>
        {
            new() { Email = "user1@test.com", Username = "updated", FirstName = "Updated", LastName = "Existing", Points = 999, IsActive = false, RegisteredAt = DateTime.UtcNow },
            new() { Email = "new1@test.com", Username = "new1", FirstName = "New", LastName = "User1", Points = 200, IsActive = true, RegisteredAt = DateTime.UtcNow },
            new() { Email = "new2@test.com", Username = "new2", FirstName = "Another", LastName = "User2", Points = 300, IsActive = true, RegisteredAt = DateTime.UtcNow }
        };

        Assert.All(mixedUsers, u => Assert.Equal(0, u.Id));

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(mixedUsers, matchOn: x => x.Email, options: options);
        }

        Assert.All(mixedUsers, u => Assert.True(u.Id > 0, $"User {u.Email} should have ID synced"));
        var existingUser = mixedUsers.First(u => u.Email == "user1@test.com");
        var newUser1 = mixedUsers.First(u => u.Email == "new1@test.com");
        var newUser2 = mixedUsers.First(u => u.Email == "new2@test.com");

        Assert.Equal(existingUserId, existingUser.Id);
        Assert.NotEqual(existingUserId, newUser1.Id);
        Assert.NotEqual(existingUserId, newUser2.Id);
        Assert.NotEqual(newUser1.Id, newUser2.Id);

        var allDbUsers = await _fixture.GetAllEntitiesAsync<UserEntity>();
        // The test creates 2 initial users (user1, user2), then upserts 3 users (user1 update, new1, new2).
        // After upsert, we should have: user1 (updated), user2 (unchanged), new1, new2 = 4 users.
        // Since BulkUpsertAsync doesn't delete, user2 should remain.
        Assert.Equal(4, allDbUsers.Count);
        Assert.Contains(allDbUsers, u => u.Id == existingUser.Id && u.Email == "user1@test.com" && u.Points == 999);
        Assert.Contains(allDbUsers, u => u.Id == newUser1.Id && u.Email == "new1@test.com");
        Assert.Contains(allDbUsers, u => u.Id == newUser2.Id && u.Email == "new2@test.com");

        // Clean up for next test
        await _fixture.ClearTableAsync<UserEntity>();

        // Test 3: Update-only scenario - entities without IDs should get IDs synced
        var initialUsers3 = new List<UserEntity>
        {
            new() { Email = "user1@test.com", Username = "user1", FirstName = "John", LastName = "Doe", Points = 100, IsActive = true, RegisteredAt = DateTime.UtcNow },
            new() { Email = "user2@test.com", Username = "user2", FirstName = "Jane", LastName = "Smith", Points = 200, IsActive = true, RegisteredAt = DateTime.UtcNow }
        };

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkInsertAsync(initialUsers3);
        }

        var dbUsers3 = await _fixture.GetAllEntitiesAsync<UserEntity>();
        var user1Id = dbUsers3.First(u => u.Email == "user1@test.com").Id;
        var user2Id = dbUsers3.First(u => u.Email == "user2@test.com").Id;

        // Create new entity objects without IDs (simulating data from external source)
        var updateUsers = new List<UserEntity>
        {
            new() { Email = "user1@test.com", Username = "user1_updated", FirstName = "Johnny", LastName = "Updated", Points = 999, IsActive = false, RegisteredAt = DateTime.UtcNow },
            new() { Email = "user2@test.com", Username = "user2_updated", FirstName = "Janet", LastName = "Updated", Points = 888, IsActive = false, RegisteredAt = DateTime.UtcNow }
        };

        Assert.All(updateUsers, u => Assert.Equal(0, u.Id));

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(updateUsers, matchOn: x => x.Email, options: options);
        }

        // Assert - Updated entities should have their IDs synced from database
        var user1 = updateUsers.First(u => u.Email == "user1@test.com");
        var user2 = updateUsers.First(u => u.Email == "user2@test.com");

        Assert.Equal(user1Id, user1.Id);
        Assert.Equal(user2Id, user2.Id);
        Assert.True(user1.Id > 0, "Updated user1 should have ID synced");
        Assert.True(user2.Id > 0, "Updated user2 should have ID synced");

        // Verify the updates were applied in database
        var updatedDbUsers = await _fixture.GetAllEntitiesAsync<UserEntity>();
        Assert.Equal(2, updatedDbUsers.Count);
        var dbUser1 = updatedDbUsers.First(u => u.Email == "user1@test.com");
        Assert.Equal("user1_updated", dbUser1.Username);
        Assert.Equal(999, dbUser1.Points);
        var dbUser2 = updatedDbUsers.First(u => u.Email == "user2@test.com");
        Assert.Equal("user2_updated", dbUser2.Username);
        Assert.Equal(888, dbUser2.Points);
    }

    [Fact]
    public async Task BulkUpsertAsync_WithIdentityOutputAndEdgeCases_ShouldHandleCorrectly()
    {
        // Test 1: Composite keys (no identity columns) - should not break
        var initialEntities = new List<CompositeKeyEntity>
        {
            new() { Key1 = 1, Key2 = "A", Data = "Initial Data A", Counter = 100 },
            new() { Key1 = 2, Key2 = "B", Data = "Initial Data B", Counter = 200 }
        };

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkInsertAsync(initialEntities);
        }

        var mixedEntities = new List<CompositeKeyEntity>
        {
            new() { Key1 = 1, Key2 = "A", Data = "Updated Data A", Counter = 999 },
            new() { Key1 = 3, Key2 = "C", Data = "New Data C", Counter = 300 },
            new() { Key1 = 4, Key2 = "D", Data = "New Data D", Counter = 400 }
        };

        var options = new BulkConfig { IdentityOutput = true };

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(mixedEntities, options: options);
        }

        var allEntities = await _fixture.GetAllEntitiesAsync<CompositeKeyEntity>();
        Assert.Equal(4, allEntities.Count);
        var entityA = allEntities.First(e => e.Key1 == 1 && e.Key2 == "A");
        Assert.Equal("Updated Data A", entityA.Data);
        Assert.Equal(999, entityA.Counter);

        // Clean up for next test
        await _fixture.ClearTableAsync<CompositeKeyEntity>();

        // Test 2: Large dataset performance
        var largeEntities = Enumerable.Range(1, 5000)
            .Select(i => new SimpleEntity
            {
                Name = $"Entity {i}",
                Value = i,
                CreatedAt = DateTime.UtcNow
            })
            .ToList();

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(largeEntities, options: options);
        }

        Assert.All(largeEntities, e => Assert.True(e.Id > 0));
        var uniqueIds = largeEntities.Select(e => e.Id).Distinct().Count();
        Assert.Equal(5000, uniqueIds);
        var count = await _fixture.GetCountAsync<SimpleEntity>();
        Assert.Equal(5000, count);
    }

    [Fact]
    public async Task BulkUpsertAsync_WithIdentityOutputAndInsertOnly_ShouldSyncOnlyInserted()
    {
        // Arrange - Insert initial data
        var initialUsers = new List<UserEntity>
        {
            new() { Email = "user1@test.com", Username = "user1", FirstName = "John", LastName = "Doe", Points = 100, IsActive = true, RegisteredAt = DateTime.UtcNow }
        };

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkInsertAsync(initialUsers);
        }

        // Try to update existing and insert new
        var upsertUsers = new List<UserEntity>
        {
            new() { Email = "user1@test.com", Username = "updated", FirstName = "Updated", LastName = "Name", Points = 999, IsActive = false, RegisteredAt = DateTime.UtcNow }, // Should be ignored
            new() { Email = "user2@test.com", Username = "user2", FirstName = "New", LastName = "User", Points = 200, IsActive = true, RegisteredAt = DateTime.UtcNow } // Should be inserted
        };

        var options = new BulkConfig { InsertOnly = true, IdentityOutput = true };

        // Act
        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(upsertUsers, matchOn: x => x.Email, options: options);
        }

        // Assert - Only user2 should have ID synced (it was inserted)
        var user2 = upsertUsers.First(u => u.Email == "user2@test.com");
        Assert.True(user2.Id > 0, "New user should have ID synced");

        var allUsers = await _fixture.GetAllEntitiesAsync<UserEntity>();
        Assert.Equal(2, allUsers.Count);
        Assert.Contains(allUsers, u => u.Id == user2.Id && u.Email == "user2@test.com");
    }


    #endregion
}
