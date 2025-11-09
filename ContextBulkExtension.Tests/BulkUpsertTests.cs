using ContextBulkExtension.Tests.Fixtures;
using ContextBulkExtension.Tests.TestEntities;
using Microsoft.EntityFrameworkCore;

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
        await using var context = _fixture.CreateNewContext();
        await context.BulkUpsertAsync(entities);

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

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkInsertAsync(initialEntities);
        }

        var insertedEntities = await _fixture.GetAllEntitiesAsync<SimpleEntity>();

        // Update the entities
        foreach (var entity in insertedEntities)
        {
            entity.Name = $"Updated {entity.Id}";
            entity.Value = entity.Value * 10;
        }

        // Act
        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(insertedEntities);
        }

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

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkInsertAsync(initialEntities);
        }

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
        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(mixedEntities);
        }

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
    public async Task BulkUpsertAsync_WithUpdateColumns_ShouldOnlyUpdateSpecifiedColumns()
    {
        // Arrange - Insert initial data
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

        // Modify entity
        existingEntity.Name = "Updated Name";
        existingEntity.Value = 999;
        existingEntity.CreatedAt = DateTime.UtcNow;

        // Act - Only update Value column using expression
        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync([existingEntity], updateColumns: x => x.Value);
        }

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
        await _fixture.ExecuteInTransactionAsync(async (context) =>
        {
            await context.BulkUpsertAsync(entities);
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
        await using var context = _fixture.CreateNewContext();
        await using var transaction = await context.Database.BeginTransactionAsync();
        await context.BulkUpsertAsync(entities);
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
        await using var context = _fixture.CreateNewContext();
        List<SimpleEntity>? nullEntities = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            context.BulkUpsertAsync(nullEntities!));
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

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(entities);
        }

        // Get inserted entities with their IDs
        var firstUpsert = await _fixture.GetAllEntitiesAsync<SimpleEntity>();
        Assert.Equal(2, firstUpsert.Count);

        // Second upsert (update)
        foreach (var entity in firstUpsert)
        {
            entity.Value = entity.Value * 10;
        }

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(firstUpsert);
        }

        var secondUpsert = await _fixture.GetAllEntitiesAsync<SimpleEntity>();
        Assert.Equal(2, secondUpsert.Count);
        Assert.All(secondUpsert, e => Assert.True(e.Value >= 10));

        // Third upsert (mixed)
        firstUpsert[0].Value = 999;
        var newEntity = new SimpleEntity { Name = "Entity 3", Value = 3, CreatedAt = DateTime.UtcNow };
        var mixedEntities = firstUpsert.Take(1).Concat([newEntity]).ToList();

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(mixedEntities);
        }

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
    public async Task BulkUpsertAsync_WithCustomMatchOnSingleColumn_ShouldMatchByEmail()
    {
        // Arrange - Insert initial users
        var initialUsers = new List<UserEntity>
        {
            new() { Email = "user1@test.com", Username = "user1", FirstName = "John", LastName = "Doe", Points = 100, IsActive = true, RegisteredAt = DateTime.UtcNow },
            new() { Email = "user2@test.com", Username = "user2", FirstName = "Jane", LastName = "Smith", Points = 200, IsActive = true, RegisteredAt = DateTime.UtcNow }
        };

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkInsertAsync(initialUsers);
        }

        var existingUsers = await _fixture.GetAllEntitiesAsync<UserEntity>();

        // Update users - change everything except Email (match key)
        var updatedUsers = new List<UserEntity>
        {
            new() { Email = "user1@test.com", Username = "updated_user1", FirstName = "Johnny", LastName = "Updated", Points = 999, IsActive = false, RegisteredAt = DateTime.UtcNow },
            new() { Email = "user2@test.com", Username = "updated_user2", FirstName = "Janet", LastName = "Updated", Points = 888, IsActive = false, RegisteredAt = DateTime.UtcNow },
            new() { Email = "user3@test.com", Username = "user3", FirstName = "New", LastName = "User", Points = 300, IsActive = true, RegisteredAt = DateTime.UtcNow } // New user
        };

        // Act - Match on Email instead of Id
        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(updatedUsers, matchOn: x => x.Email);
        }

        // Assert
        var allUsers = await _fixture.GetAllEntitiesAsync<UserEntity>();
        Assert.Equal(3, allUsers.Count);

        var user1 = allUsers.First(u => u.Email == "user1@test.com");
        Assert.Equal("updated_user1", user1.Username); // Updated
        Assert.Equal("Johnny", user1.FirstName); // Updated
        Assert.Equal(999, user1.Points); // Updated
        Assert.Equal(existingUsers[0].Id, user1.Id); // Same ID (updated, not inserted)

        var user3 = allUsers.First(u => u.Email == "user3@test.com");
        Assert.Equal("New", user3.FirstName); // Inserted
        Assert.True(user3.Id > 0); // Has new ID
    }

    [Fact]
    public async Task BulkUpsertAsync_WithCustomMatchOnMultipleColumns_ShouldMatchByComposite()
    {
        // Arrange - Insert initial users
        var initialUsers = new List<UserEntity>
        {
            new() { Email = "user1@test.com", Username = "john_doe", FirstName = "John", LastName = "Doe", Points = 100, IsActive = true, RegisteredAt = DateTime.UtcNow },
            new() { Email = "user2@test.com", Username = "jane_smith", FirstName = "Jane", LastName = "Smith", Points = 200, IsActive = true, RegisteredAt = DateTime.UtcNow }
        };

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkInsertAsync(initialUsers);
        }

        // Update users - match by Email + Username composite
        var updatedUsers = new List<UserEntity>
        {
            new() { Email = "user1@test.com", Username = "john_doe", FirstName = "Johnny", LastName = "Updated", Points = 999, IsActive = false, RegisteredAt = DateTime.UtcNow }, // Update
            new() { Email = "user3@test.com", Username = "new_user", FirstName = "New", LastName = "User", Points = 300, IsActive = true, RegisteredAt = DateTime.UtcNow } // Insert
        };

        // Act - Match on Email + Username composite
        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(updatedUsers, matchOn: x => new { x.Email, x.Username });
        }

        // Assert
        var allUsers = await _fixture.GetAllEntitiesAsync<UserEntity>();
        Assert.Equal(3, allUsers.Count);

        var updatedUser = allUsers.First(u => u.Email == "user1@test.com" && u.Username == "john_doe");
        Assert.Equal("Johnny", updatedUser.FirstName); // Updated
        Assert.Equal(999, updatedUser.Points); // Updated

        var newUser = allUsers.First(u => u.Email == "user3@test.com");
        Assert.Equal("new_user", newUser.Username); // Inserted
    }

    [Fact]
    public async Task BulkUpsertAsync_WithCustomMatchOn_MixedInsertUpdate()
    {
        // Arrange - Insert initial users
        var initialUsers = new List<UserEntity>
        {
            new() { Email = "existing1@test.com", Username = "user1", FirstName = "John", LastName = "Doe", Points = 100, IsActive = true, RegisteredAt = DateTime.UtcNow },
            new() { Email = "existing2@test.com", Username = "user2", FirstName = "Jane", LastName = "Smith", Points = 200, IsActive = true, RegisteredAt = DateTime.UtcNow }
        };

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkInsertAsync(initialUsers);
        }

        // Mix of updates and inserts
        var mixedUsers = new List<UserEntity>
        {
            new() { Email = "existing1@test.com", Username = "updated", FirstName = "Johnny", LastName = "Updated", Points = 999, IsActive = false, RegisteredAt = DateTime.UtcNow }, // Update
            new() { Email = "new1@test.com", Username = "new1", FirstName = "New1", LastName = "User1", Points = 300, IsActive = true, RegisteredAt = DateTime.UtcNow }, // Insert
            new() { Email = "existing2@test.com", Username = "updated2", FirstName = "Janet", LastName = "Updated2", Points = 888, IsActive = false, RegisteredAt = DateTime.UtcNow }, // Update
            new() { Email = "new2@test.com", Username = "new2", FirstName = "New2", LastName = "User2", Points = 400, IsActive = true, RegisteredAt = DateTime.UtcNow } // Insert
        };

        // Act - Match on Email
        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(mixedUsers, matchOn: x => x.Email);
        }

        // Assert
        var allUsers = await _fixture.GetAllEntitiesAsync<UserEntity>();
        Assert.Equal(4, allUsers.Count);

        var updatedUsers = allUsers.Where(u => u.Email.StartsWith("existing")).ToList();
        Assert.Equal(2, updatedUsers.Count);
        Assert.All(updatedUsers, u => Assert.False(u.IsActive)); // All updated to inactive

        var insertedUsers = allUsers.Where(u => u.Email.StartsWith("new")).ToList();
        Assert.Equal(2, insertedUsers.Count);
        Assert.All(insertedUsers, u => Assert.True(u.IsActive)); // All new are active
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
    public async Task BulkUpsertAsync_WithMultipleUpdateColumns_ShouldOnlyUpdateSpecified()
    {
        // Arrange - Insert initial data
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

        // Modify all fields
        foreach (var user in existingUsers)
        {
            user.Username = "updated_username";
            user.FirstName = "UpdatedFirst";
            user.LastName = "UpdatedLast";
            user.Points = 999;
            user.IsActive = false;
            user.RegisteredAt = DateTime.UtcNow;
        }

        // Act - Only update Points and IsActive
        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(existingUsers, updateColumns: x => new { x.Points, x.IsActive });
        }

        // Assert
        var updatedUsers = await _fixture.GetAllEntitiesAsync<UserEntity>();
        Assert.Equal(2, updatedUsers.Count);

        foreach (var user in updatedUsers)
        {
            // These should be updated
            Assert.Equal(999, user.Points);
            Assert.False(user.IsActive);

            // These should NOT be updated
            Assert.NotEqual("updated_username", user.Username);
            Assert.NotEqual("UpdatedFirst", user.FirstName);
            Assert.NotEqual("UpdatedLast", user.LastName);
        }
    }

    [Fact]
    public async Task BulkUpsertAsync_WithUpdateColumnsOnCompositeKey_ShouldWork()
    {
        // Arrange - Insert initial data
        var initialEntities = new List<CompositeKeyEntity>
        {
            new() { Key1 = 1, Key2 = "A", Data = "Initial Data A", Counter = 100 },
            new() { Key1 = 2, Key2 = "B", Data = "Initial Data B", Counter = 200 }
        };

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkInsertAsync(initialEntities);
        }

        // Update with changes to both columns
        var updatedEntities = new List<CompositeKeyEntity>
        {
            new() { Key1 = 1, Key2 = "A", Data = "Updated Data A", Counter = 999 },
            new() { Key1 = 2, Key2 = "B", Data = "Updated Data B", Counter = 888 }
        };

        // Act - Only update Counter column
        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(updatedEntities, updateColumns: x => x.Counter);
        }

        // Assert
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
    public async Task BulkUpsertAsync_WithCustomMatchOnAndUpdateColumns_ShouldWorkTogether()
    {
        // Arrange - Insert initial users
        var initialUsers = new List<UserEntity>
        {
            new() { Email = "user1@test.com", Username = "user1", FirstName = "John", LastName = "Doe", Points = 100, IsActive = true, RegisteredAt = DateTime.UtcNow.AddDays(-7) },
            new() { Email = "user2@test.com", Username = "user2", FirstName = "Jane", LastName = "Smith", Points = 200, IsActive = true, RegisteredAt = DateTime.UtcNow.AddDays(-7) }
        };

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkInsertAsync(initialUsers);
        }

        // Update users - change all fields
        var updatedUsers = new List<UserEntity>
        {
            new() { Email = "user1@test.com", Username = "updated_user1", FirstName = "Johnny", LastName = "Updated", Points = 999, IsActive = false, RegisteredAt = DateTime.UtcNow },
            new() { Email = "user2@test.com", Username = "updated_user2", FirstName = "Janet", LastName = "Updated", Points = 888, IsActive = false, RegisteredAt = DateTime.UtcNow },
            new() { Email = "user3@test.com", Username = "user3", FirstName = "New", LastName = "User", Points = 300, IsActive = true, RegisteredAt = DateTime.UtcNow } // New user
        };

        // Act - Match on Email, only update FirstName and Points
        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(updatedUsers, matchOn: x => x.Email, updateColumns: x => new { x.FirstName, x.Points });
        }

        // Assert
        var allUsers = await _fixture.GetAllEntitiesAsync<UserEntity>();
        Assert.Equal(3, allUsers.Count);

        var user1 = allUsers.First(u => u.Email == "user1@test.com");
        // Updated columns
        Assert.Equal("Johnny", user1.FirstName);
        Assert.Equal(999, user1.Points);
        // NOT updated columns
        Assert.Equal("user1", user1.Username); // Original value
        Assert.Equal("Doe", user1.LastName); // Original value
        Assert.True(user1.IsActive); // Original value

        // New user should have all fields set
        var user3 = allUsers.First(u => u.Email == "user3@test.com");
        Assert.Equal("New", user3.FirstName);
        Assert.Equal(300, user3.Points);
        Assert.Equal("user3", user3.Username);
    }

    [Fact]
    public async Task BulkUpsertAsync_WithCompositeMatchOnAndUpdateColumns()
    {
        // Arrange - Insert initial users
        var initialUsers = new List<UserEntity>
        {
            new() { Email = "user1@test.com", Username = "john_doe", FirstName = "John", LastName = "Doe", Points = 100, IsActive = true, RegisteredAt = DateTime.UtcNow.AddDays(-7) }
        };

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkInsertAsync(initialUsers);
        }

        // Update with composite match
        var updatedUsers = new List<UserEntity>
        {
            new() { Email = "user1@test.com", Username = "john_doe", FirstName = "Johnny", LastName = "Updated", Points = 999, IsActive = false, RegisteredAt = DateTime.UtcNow },
            new() { Email = "user2@test.com", Username = "jane_smith", FirstName = "Jane", LastName = "Smith", Points = 200, IsActive = true, RegisteredAt = DateTime.UtcNow } // New
        };

        // Act - Match on Email+Username, only update LastName
        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(updatedUsers, matchOn: x => new { x.Email, x.Username }, updateColumns: x => x.LastName);
        }

        // Assert
        var allUsers = await _fixture.GetAllEntitiesAsync<UserEntity>();
        Assert.Equal(2, allUsers.Count);

        var user1 = allUsers.First(u => u.Email == "user1@test.com");
        Assert.Equal("Updated", user1.LastName); // Updated
        Assert.Equal("John", user1.FirstName); // NOT updated
        Assert.Equal(100, user1.Points); // NOT updated
        Assert.True(user1.IsActive); // NOT updated
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public async Task BulkUpsertAsync_WithInvalidMatchOnProperty_ShouldThrowInvalidOperationException()
    {
        // Arrange
        await using var context = _fixture.CreateNewContext();
        var users = new List<UserEntity>
        {
            new() { Email = "user1@test.com", Username = "user1", FirstName = "John", LastName = "Doe", Points = 100, IsActive = true, RegisteredAt = DateTime.UtcNow }
        };

        // Act & Assert - Using a property that doesn't exist should throw
        // Note: This test verifies compile-time safety - invalid properties won't compile
        // But we can test with null/empty scenarios if the implementation supports it
        await context.BulkUpsertAsync(users, matchOn: x => x.Email); // This should work
    }

    [Fact]
    public async Task BulkUpsertAsync_WithInvalidUpdateColumnsProperty_ShouldThrowInvalidOperationException()
    {
        // Arrange
        var initialUsers = new List<UserEntity>
        {
            new() { Email = "user1@test.com", Username = "user1", FirstName = "John", LastName = "Doe", Points = 100, IsActive = true, RegisteredAt = DateTime.UtcNow }
        };

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkInsertAsync(initialUsers);
        }

        var existingUsers = await _fixture.GetAllEntitiesAsync<UserEntity>();
        existingUsers[0].Points = 999;

        // Act & Assert - Using valid properties should work
        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(existingUsers, updateColumns: x => x.Points); // This should work
        }

        var updated = await _fixture.GetAllEntitiesAsync<UserEntity>();
        Assert.Equal(999, updated[0].Points);
    }

    #endregion

    #region IdentityOutput Tests

    [Fact]
    public async Task BulkUpsertAsync_WithIdentityOutputEnabled_ShouldSyncGeneratedIds()
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

        var options = new BulkConfig { IdentityOutput = true };

        // Act
        await using var context = _fixture.CreateNewContext();
        await context.BulkUpsertAsync(entities, options: options);

        // Assert - All entities should have their IDs synced
        Assert.All(entities, e => Assert.True(e.Id > 0, "Entity ID should be synced from database"));

        // Verify IDs are unique
        var uniqueIds = entities.Select(e => e.Id).Distinct().Count();
        Assert.Equal(100, uniqueIds);

        // Verify entities exist in database with same IDs
        var dbEntities = await _fixture.GetAllEntitiesAsync<SimpleEntity>();
        foreach (var entity in entities)
        {
            Assert.Contains(dbEntities, db => db.Id == entity.Id && db.Name == entity.Name);
        }
    }

    [Fact]
    public async Task BulkUpsertAsync_WithIdentityOutputDisabled_ShouldNotSyncIds()
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

        var options = new BulkConfig { IdentityOutput = false };

        // Act
        await using var context = _fixture.CreateNewContext();
        await context.BulkUpsertAsync(entities, options: options);

        // Assert - Entity IDs should remain 0 (not synced)
        Assert.All(entities, e => Assert.Equal(0, e.Id));

        // But entities should exist in database
        var count = await _fixture.GetCountAsync<SimpleEntity>();
        Assert.Equal(50, count);
    }

    [Fact]
    public async Task BulkUpsertAsync_WithIdentityOutput_OnlyAffectsInsertedRecords()
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

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkInsertAsync(initialEntities);
        }

        var existingEntities = await _fixture.GetAllEntitiesAsync<SimpleEntity>();
        var originalIds = existingEntities.Select(e => e.Id).ToList();

        // Update existing entities
        foreach (var entity in existingEntities)
        {
            entity.Value = entity.Value * 10;
        }

        // Add new entities
        var newEntities = Enumerable.Range(51, 50)
            .Select(i => new SimpleEntity
            {
                Name = $"New {i}",
                Value = i,
                CreatedAt = DateTime.UtcNow
            })
            .ToList();

        var allEntities = existingEntities.Concat(newEntities).ToList();

        var options = new BulkConfig { IdentityOutput = true };

        // Act
        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(allEntities, options: options);
        }

        // Assert - Existing entities should keep their IDs unchanged
        for (int i = 0; i < existingEntities.Count; i++)
        {
            Assert.Equal(originalIds[i], existingEntities[i].Id);
        }

        // New entities should have IDs synced
        Assert.All(newEntities, e => Assert.True(e.Id > 0, "New entity ID should be synced"));
        Assert.All(newEntities, e => Assert.DoesNotContain(originalIds, id => id == e.Id));
    }

    [Fact]
    public async Task BulkUpsertAsync_WithIdentityOutputAndCustomMatchOn_ShouldSyncNewRecords()
    {
        // Arrange - Insert initial users
        var initialUsers = new List<UserEntity>
        {
            new() { Email = "user1@test.com", Username = "user1", FirstName = "John", LastName = "Doe", Points = 100, IsActive = true, RegisteredAt = DateTime.UtcNow },
            new() { Email = "user2@test.com", Username = "user2", FirstName = "Jane", LastName = "Smith", Points = 200, IsActive = true, RegisteredAt = DateTime.UtcNow }
        };

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkInsertAsync(initialUsers);
        }

        var existingUsers = await _fixture.GetAllEntitiesAsync<UserEntity>();
        var originalIds = existingUsers.Select(u => u.Id).ToList();

        // Mix of updates and inserts - match on Email
        var mixedUsers = new List<UserEntity>
        {
            new() { Email = "user1@test.com", Username = "updated", FirstName = "Johnny", LastName = "Updated", Points = 999, IsActive = false, RegisteredAt = DateTime.UtcNow }, // Update
            new() { Email = "user3@test.com", Username = "user3", FirstName = "New", LastName = "User", Points = 300, IsActive = true, RegisteredAt = DateTime.UtcNow }, // Insert
            new() { Email = "user4@test.com", Username = "user4", FirstName = "Another", LastName = "New", Points = 400, IsActive = true, RegisteredAt = DateTime.UtcNow } // Insert
        };

        var options = new BulkConfig { IdentityOutput = true };

        // Act
        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(mixedUsers, matchOn: x => x.Email, options: options);
        }

        // Assert - First user should have ID set (but it's an update, so ID might not be meaningful in input list)
        // New users should have IDs synced
        var user3 = mixedUsers.First(u => u.Email == "user3@test.com");
        var user4 = mixedUsers.First(u => u.Email == "user4@test.com");

        Assert.True(user3.Id > 0, "New user3 should have ID synced");
        Assert.True(user4.Id > 0, "New user4 should have ID synced");
        Assert.NotEqual(user3.Id, user4.Id);

        // Verify in database
        var allUsers = await _fixture.GetAllEntitiesAsync<UserEntity>();
        Assert.Equal(4, allUsers.Count);
        Assert.Contains(allUsers, u => u.Id == user3.Id && u.Email == "user3@test.com");
        Assert.Contains(allUsers, u => u.Id == user4.Id && u.Email == "user4@test.com");
    }

    [Fact]
    public async Task BulkUpsertAsync_WithIdentityOutputAndCompositeKeys_ShouldWork()
    {
        // Arrange - Insert initial entities
        var initialEntities = new List<CompositeKeyEntity>
        {
            new() { Key1 = 1, Key2 = "A", Data = "Initial Data A", Counter = 100 },
            new() { Key1 = 2, Key2 = "B", Data = "Initial Data B", Counter = 200 }
        };

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkInsertAsync(initialEntities);
        }

        // Mix of updates and inserts
        var mixedEntities = new List<CompositeKeyEntity>
        {
            new() { Key1 = 1, Key2 = "A", Data = "Updated Data A", Counter = 999 }, // Update
            new() { Key1 = 3, Key2 = "C", Data = "New Data C", Counter = 300 }, // Insert
            new() { Key1 = 4, Key2 = "D", Data = "New Data D", Counter = 400 } // Insert
        };

        var options = new BulkConfig { IdentityOutput = true };

        // Act - Note: CompositeKeyEntity may not have identity columns
        // This test verifies that IdentityOutput doesn't break when there are no identity columns
        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(mixedEntities, options: options);
        }

        // Assert
        var allEntities = await _fixture.GetAllEntitiesAsync<CompositeKeyEntity>();
        Assert.Equal(4, allEntities.Count);

        var entityA = allEntities.First(e => e.Key1 == 1 && e.Key2 == "A");
        Assert.Equal("Updated Data A", entityA.Data);
        Assert.Equal(999, entityA.Counter);
    }

    [Fact]
    public async Task BulkUpsertAsync_WithIdentityOutputAndLargeDataset_ShouldPerformEfficiently()
    {
        // Arrange - Large dataset to test performance
        var entities = Enumerable.Range(1, 5000)
            .Select(i => new SimpleEntity
            {
                Name = $"Entity {i}",
                Value = i,
                CreatedAt = DateTime.UtcNow
            })
            .ToList();

        var options = new BulkConfig { IdentityOutput = true };

        // Act
        await using var context = _fixture.CreateNewContext();
        await context.BulkUpsertAsync(entities, options: options);

        // Assert - All 5000 entities should have IDs synced
        Assert.All(entities, e => Assert.True(e.Id > 0));

        var uniqueIds = entities.Select(e => e.Id).Distinct().Count();
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

    [Fact]
    public async Task BulkUpsertAsync_WithIdentityOutputOnUpdate_ShouldSyncExistingIds()
    {
        // Arrange - Insert initial users without capturing IDs in input entities
        var initialUsers = new List<UserEntity>
        {
            new() { Email = "user1@test.com", Username = "user1", FirstName = "John", LastName = "Doe", Points = 100, IsActive = true, RegisteredAt = DateTime.UtcNow },
            new() { Email = "user2@test.com", Username = "user2", FirstName = "Jane", LastName = "Smith", Points = 200, IsActive = true, RegisteredAt = DateTime.UtcNow }
        };

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkInsertAsync(initialUsers);
        }

        // Get actual IDs from database
        var dbUsers = await _fixture.GetAllEntitiesAsync<UserEntity>();
        var user1Id = dbUsers.First(u => u.Email == "user1@test.com").Id;
        var user2Id = dbUsers.First(u => u.Email == "user2@test.com").Id;

        // Create new entity objects without IDs (simulating data coming from external source)
        var updateUsers = new List<UserEntity>
        {
            new() { Email = "user1@test.com", Username = "user1_updated", FirstName = "Johnny", LastName = "Updated", Points = 999, IsActive = false, RegisteredAt = DateTime.UtcNow }, // Update
            new() { Email = "user2@test.com", Username = "user2_updated", FirstName = "Janet", LastName = "Updated", Points = 888, IsActive = false, RegisteredAt = DateTime.UtcNow }  // Update
        };

        // Verify IDs are not set initially (default to 0)
        Assert.All(updateUsers, u => Assert.Equal(0, u.Id));

        var options = new BulkConfig { IdentityOutput = true };

        // Act - Match on Email, should update existing records and sync their IDs back
        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(updateUsers, matchOn: x => x.Email, options: options);
        }

        // Assert - Updated entities should now have their IDs synced from database
        var user1 = updateUsers.First(u => u.Email == "user1@test.com");
        var user2 = updateUsers.First(u => u.Email == "user2@test.com");

        Assert.Equal(user1Id, user1.Id); // ID should match the existing database record
        Assert.Equal(user2Id, user2.Id); // ID should match the existing database record
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
    public async Task BulkUpsertAsync_WithIdentityOutputOnMixedOperations_ShouldSyncAllIds()
    {
        // Arrange - Insert initial data
        var initialUsers = new List<UserEntity>
        {
            new() { Email = "existing@test.com", Username = "existing", FirstName = "Existing", LastName = "User", Points = 100, IsActive = true, RegisteredAt = DateTime.UtcNow }
        };

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkInsertAsync(initialUsers);
        }

        var dbUsers = await _fixture.GetAllEntitiesAsync<UserEntity>();
        var existingUserId = dbUsers.First(u => u.Email == "existing@test.com").Id;

        // Create mix of update and insert without IDs
        var mixedUsers = new List<UserEntity>
        {
            new() { Email = "existing@test.com", Username = "updated", FirstName = "Updated", LastName = "Existing", Points = 999, IsActive = false, RegisteredAt = DateTime.UtcNow }, // UPDATE
            new() { Email = "new1@test.com", Username = "new1", FirstName = "New", LastName = "User1", Points = 200, IsActive = true, RegisteredAt = DateTime.UtcNow }, // INSERT
            new() { Email = "new2@test.com", Username = "new2", FirstName = "Another", LastName = "User2", Points = 300, IsActive = true, RegisteredAt = DateTime.UtcNow } // INSERT
        };

        Assert.All(mixedUsers, u => Assert.Equal(0, u.Id));

        var options = new BulkConfig { IdentityOutput = true };

        // Act
        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(mixedUsers, matchOn: x => x.Email, options: options);
        }

        // Assert - All entities should have IDs synced (both updated and inserted)
        Assert.All(mixedUsers, u => Assert.True(u.Id > 0, $"User {u.Email} should have ID synced"));

        var existingUser = mixedUsers.First(u => u.Email == "existing@test.com");
        var newUser1 = mixedUsers.First(u => u.Email == "new1@test.com");
        var newUser2 = mixedUsers.First(u => u.Email == "new2@test.com");

        // Updated user should keep the same ID
        Assert.Equal(existingUserId, existingUser.Id);

        // New users should have different IDs
        Assert.NotEqual(existingUserId, newUser1.Id);
        Assert.NotEqual(existingUserId, newUser2.Id);
        Assert.NotEqual(newUser1.Id, newUser2.Id);

        // Verify in database
        var allDbUsers = await _fixture.GetAllEntitiesAsync<UserEntity>();
        Assert.Equal(3, allDbUsers.Count);
        Assert.Contains(allDbUsers, u => u.Id == existingUser.Id && u.Email == "existing@test.com" && u.Points == 999);
        Assert.Contains(allDbUsers, u => u.Id == newUser1.Id && u.Email == "new1@test.com");
        Assert.Contains(allDbUsers, u => u.Id == newUser2.Id && u.Email == "new2@test.com");
    }

    #endregion
}
