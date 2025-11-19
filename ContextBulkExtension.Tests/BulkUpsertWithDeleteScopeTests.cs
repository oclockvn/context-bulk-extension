using ContextBulkExtension.Tests.Fixtures;
using ContextBulkExtension.Tests.TestEntities;
using Microsoft.EntityFrameworkCore;

namespace ContextBulkExtension.Tests;

public class BulkUpsertWithDeleteScopeTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private readonly DatabaseFixture _fixture = fixture;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Clean up tables after each test
        await _fixture.ClearTableAsync<MetricEntity>();
        await _fixture.ClearTableAsync<SimpleEntity>();
    }

    [Fact]
    public async Task BulkUpsertWithDeleteScopeAsync_WithEmptyCollection_ShouldNotDelete()
    {
        // Arrange - Seed existing data
        var existingData = new List<MetricEntity>
        {
            new() { AccountId = 123, Metric = "TOU", Date = DateTime.UtcNow, Value = 100, Category = "Energy" },
            new() { AccountId = 123, Metric = "Demand", Date = DateTime.UtcNow, Value = 50, Category = "Energy" }
        };
        await _fixture.SeedDataAsync(existingData);

        // Act - Call with empty list
        await using var context = _fixture.CreateNewContext();
        await context.BulkUpsertWithDeleteScopeAsync(
            new List<MetricEntity>(),
            deleteScope: m => m.AccountId == 123);

        // Assert - Existing data should remain untouched
        var count = await _fixture.GetCountAsync<MetricEntity>();
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task BulkUpsertWithDeleteScopeAsync_ShouldHandleDifferentExpressionPatterns()
    {
        // Ensure clean state at start
        await _fixture.ClearTableAsync<MetricEntity>();

        // Test Case 1: Simple equality expression
        {
            // Arrange - Seed data for multiple accounts
            var existingData = new List<MetricEntity>
            {
                // Account 123 - should be managed by deleteScope
                new() { AccountId = 123, Metric = "TOU", Date = DateTime.UtcNow.Date, Value = 100, Category = "Energy" },
                new() { AccountId = 123, Metric = "Demand", Date = DateTime.UtcNow.Date, Value = 50, Category = "Energy" },
                // Account 456 - should NOT be touched
                new() { AccountId = 456, Metric = "TOU", Date = DateTime.UtcNow.Date, Value = 200, Category = "Energy" },
                new() { AccountId = 456, Metric = "Demand", Date = DateTime.UtcNow.Date, Value = 75, Category = "Energy" }
            };
            await _fixture.SeedDataAsync(existingData);

            // Act - Upsert new data for Account 123, delete old TOU and Demand, keep only the new one
            var newData = new List<MetricEntity>
            {
                new() { AccountId = 123, Metric = "Solar", Date = DateTime.UtcNow.Date, Value = 150, Category = "Renewable" }
            };

            await using var context = _fixture.CreateNewContext();
            await context.BulkUpsertWithDeleteScopeAsync(
                newData,
                deleteScope: m => m.AccountId == 123);

            // Assert
            var allData = await _fixture.GetAllEntitiesAsync<MetricEntity>();
            Assert.Equal(3, allData.Count); // 1 new for Account 123, 2 unchanged for Account 456

            // Account 123 should only have Solar
            var account123Data = allData.Where(m => m.AccountId == 123).ToList();
            Assert.Single(account123Data);
            Assert.Equal("Solar", account123Data[0].Metric);

            // Account 456 should remain unchanged
            var account456Data = allData.Where(m => m.AccountId == 456).ToList();
            Assert.Equal(2, account456Data.Count);
            Assert.Contains(account456Data, m => m.Metric == "TOU");
            Assert.Contains(account456Data, m => m.Metric == "Demand");
        }

        // Clean up for next test case
        await _fixture.ClearTableAsync<MetricEntity>();

        // Test Case 2: Complex AND expression
        {
            // Arrange - Seed data with multiple AccountIds, Metrics, and Categories
            var baseDate = DateTime.UtcNow.Date;
            var existingData = new List<MetricEntity>
            {
                // Account 123, Metric TOU - should be managed by deleteScope
                new() { AccountId = 123, Metric = "TOU", Date = baseDate.AddDays(-2), Value = 100, Category = "Energy" },
                new() { AccountId = 123, Metric = "TOU", Date = baseDate.AddDays(-1), Value = 110, Category = "Energy" },
                // Account 123, Metric Demand - should NOT be touched (different metric)
                new() { AccountId = 123, Metric = "Demand", Date = baseDate.AddDays(-2), Value = 50, Category = "Energy" },
                // Account 456, Metric TOU - should NOT be touched (different account)
                new() { AccountId = 456, Metric = "TOU", Date = baseDate.AddDays(-2), Value = 200, Category = "Energy" }
            };
            await _fixture.SeedDataAsync(existingData);

            // Act - Upsert new TOU data for Account 123
            var newData = new List<MetricEntity>
            {
                new() { AccountId = 123, Metric = "TOU", Date = baseDate, Value = 120, Category = "Energy" }
            };

            await using var context = _fixture.CreateNewContext();
            await context.BulkUpsertWithDeleteScopeAsync(
                newData,
                deleteScope: m => m.AccountId == 123 && m.Metric == "TOU");

            // Assert
            var allData = await _fixture.GetAllEntitiesAsync<MetricEntity>();
            Assert.Equal(3, allData.Count); // 1 new TOU for 123, 1 Demand for 123, 1 TOU for 456

            // Account 123, TOU - should only have the new record
            var account123TOU = allData.Where(m => m.AccountId == 123 && m.Metric == "TOU").ToList();
            Assert.Single(account123TOU);
            Assert.Equal(baseDate, account123TOU[0].Date);
            Assert.Equal(120, account123TOU[0].Value);

            // Account 123, Demand - should remain unchanged
            var account123Demand = allData.Where(m => m.AccountId == 123 && m.Metric == "Demand").ToList();
            Assert.Single(account123Demand);

            // Account 456 - should remain unchanged
            var account456Data = allData.Where(m => m.AccountId == 456).ToList();
            Assert.Single(account456Data);
        }

        // Clean up for next test case
        await _fixture.ClearTableAsync<MetricEntity>();

        // Test Case 3: OR expression
        {
            // Arrange - Seed data with multiple categories
            var existingData = new List<MetricEntity>
            {
                // Category "Energy" - should be deleted
                new() { AccountId = 123, Metric = "TOU", Date = DateTime.UtcNow.Date, Value = 100, Category = "Energy" },
                // Category "Renewable" - should be deleted
                new() { AccountId = 123, Metric = "Solar", Date = DateTime.UtcNow.Date, Value = 50, Category = "Renewable" },
                // Category "Other" - should NOT be deleted
                new() { AccountId = 123, Metric = "Usage", Date = DateTime.UtcNow.Date, Value = 75, Category = "Other" }
            };
            await _fixture.SeedDataAsync(existingData);

            // Act - Upsert new data with deleteScope using OR
            var newData = new List<MetricEntity>
            {
                new() { AccountId = 123, Metric = "NewTOU", Date = DateTime.UtcNow.Date, Value = 200, Category = "Energy" }
            };

            await using var context = _fixture.CreateNewContext();
            await context.BulkUpsertWithDeleteScopeAsync(
                newData,
                deleteScope: m => m.Category == "Energy" || m.Category == "Renewable");

            // Assert
            var allData = await _fixture.GetAllEntitiesAsync<MetricEntity>();
            Assert.Equal(2, allData.Count); // 1 new Energy, 1 unchanged Other

            // Should have the new Energy entry
            Assert.Contains(allData, m => m.Metric == "NewTOU" && m.Category == "Energy");

            // Should have the Other entry
            Assert.Contains(allData, m => m.Category == "Other");

            // Should NOT have old Energy or Renewable
            Assert.DoesNotContain(allData, m => m.Metric == "TOU");
            Assert.DoesNotContain(allData, m => m.Metric == "Solar");
        }
    }

    [Fact]
    public async Task BulkUpsertWithDeleteScopeAsync_WithNullDeleteScope_ShouldDeleteAllNotMatched()
    {
        // Arrange - Seed existing data
        var existingData = new List<SimpleEntity>
        {
            new() { Name = "Entity1", Value = 1, CreatedAt = DateTime.UtcNow },
            new() { Name = "Entity2", Value = 2, CreatedAt = DateTime.UtcNow },
            new() { Name = "Entity3", Value = 3, CreatedAt = DateTime.UtcNow }
        };
        await _fixture.SeedDataAsync(existingData);
        var seededData = await _fixture.GetAllEntitiesAsync<SimpleEntity>();

        // Act - Upsert with only one entity (matching Entity2), no deleteScope
        var upsertData = new List<SimpleEntity>
        {
            new() { Id = seededData[1].Id, Name = "Entity2Updated", Value = 20, CreatedAt = DateTime.UtcNow }
        };

        await using var context = _fixture.CreateNewContext();
        await context.BulkUpsertWithDeleteScopeAsync(
            upsertData,
            deleteScope: null); // This will delete ALL records not in upsertData

        // Assert - Only Entity2 should remain
        var allData = await _fixture.GetAllEntitiesAsync<SimpleEntity>();
        Assert.Single(allData);
        Assert.Equal("Entity2Updated", allData[0].Name);
        Assert.Equal(20, allData[0].Value);
    }

    [Fact]
    public async Task BulkUpsertWithDeleteScopeAsync_ShouldHandleDifferentComparisonTypes()
    {
        // Ensure clean state at start
        await _fixture.ClearTableAsync<MetricEntity>();

        // Test Case 1: Date comparison
        {
            // Arrange - Seed data with different dates
            var oldDate = DateTime.UtcNow.Date.AddDays(-10);
            var recentDate = DateTime.UtcNow.Date.AddDays(-2);
            var today = DateTime.UtcNow.Date;

            var existingData = new List<MetricEntity>
            {
                new() { AccountId = 123, Metric = "TOU", Date = oldDate, Value = 100, Category = "Energy" },
                new() { AccountId = 123, Metric = "TOU", Date = recentDate, Value = 110, Category = "Energy" },
                new() { AccountId = 123, Metric = "TOU", Date = today, Value = 120, Category = "Energy" }
            };
            await _fixture.SeedDataAsync(existingData);

            // Act - Upsert new data, delete only records older than 5 days
            var cutoffDate = DateTime.UtcNow.Date.AddDays(-5);
            var newData = new List<MetricEntity>
            {
                new() { AccountId = 123, Metric = "TOU", Date = today.AddDays(1), Value = 130, Category = "Energy" }
            };

            await using var context = _fixture.CreateNewContext();
            await context.BulkUpsertWithDeleteScopeAsync(
                newData,
                deleteScope: m => m.Date < cutoffDate);

            // Assert
            var allData = await _fixture.GetAllEntitiesAsync<MetricEntity>();
            Assert.Equal(3, allData.Count); // old record deleted, recent and today kept, new one added

            // Old record should be deleted
            Assert.DoesNotContain(allData, m => m.Date == oldDate);

            // Recent and today should remain
            Assert.Contains(allData, m => m.Date == recentDate);
            Assert.Contains(allData, m => m.Date == today);

            // New record should be added
            Assert.Contains(allData, m => m.Date == today.AddDays(1));
        }

        // Clean up for next test case
        await _fixture.ClearTableAsync<MetricEntity>();

        // Test Case 2: Null value comparison
        {
            // Arrange - Seed data with null and non-null categories
            var existingData = new List<MetricEntity>
            {
                new() { AccountId = 123, Metric = "TOU", Date = DateTime.UtcNow.Date, Value = 100, Category = null },
                new() { AccountId = 123, Metric = "Demand", Date = DateTime.UtcNow.Date, Value = 50, Category = "Energy" }
            };
            await _fixture.SeedDataAsync(existingData);

            // Act - Upsert new data, delete only records with null category
            var newData = new List<MetricEntity>
            {
                new() { AccountId = 123, Metric = "Solar", Date = DateTime.UtcNow.Date, Value = 75, Category = "Renewable" }
            };

            await using var context = _fixture.CreateNewContext();
            await context.BulkUpsertWithDeleteScopeAsync(
                newData,
                deleteScope: m => m.Category == null);

            // Assert
            var allData = await _fixture.GetAllEntitiesAsync<MetricEntity>();
            Assert.Equal(2, allData.Count); // null category deleted, Energy and Renewable remain

            // Should NOT have null category
            Assert.DoesNotContain(allData, m => m.Category == null);

            // Should have Energy and Renewable
            Assert.Contains(allData, m => m.Category == "Energy");
            Assert.Contains(allData, m => m.Category == "Renewable");
        }
    }

    [Fact]
    public async Task BulkUpsertWithDeleteScopeAsync_ShouldInsertUpdateAndDelete()
    {
        // Arrange - Seed existing data for Account 123
        var existingData = new List<MetricEntity>
        {
            new() { AccountId = 123, Metric = "TOU", Date = DateTime.UtcNow.Date, Value = 100, Category = "Energy" },
            new() { AccountId = 123, Metric = "Demand", Date = DateTime.UtcNow.Date, Value = 50, Category = "Energy" },
            new() { AccountId = 123, Metric = "Solar", Date = DateTime.UtcNow.Date, Value = 25, Category = "Renewable" }
        };
        await _fixture.SeedDataAsync(existingData);
        var seededData = await _fixture.GetAllEntitiesAsync<MetricEntity>();

        // Act - Upsert with mixed operations:
        // 1. Update TOU (existing, will update)
        // 2. Insert Wind (new)
        // 3. Delete Demand and Solar (not in upsert list, within deleteScope)
        var upsertData = new List<MetricEntity>
        {
            // Update existing TOU
            new() { Id = seededData.First(m => m.Metric == "TOU").Id, AccountId = 123, Metric = "TOU", Date = DateTime.UtcNow.Date, Value = 999, Category = "Energy" },
            // Insert new Wind
            new() { AccountId = 123, Metric = "Wind", Date = DateTime.UtcNow.Date, Value = 888, Category = "Renewable" }
        };

        await using var context = _fixture.CreateNewContext();
        await context.BulkUpsertWithDeleteScopeAsync(
            upsertData,
            deleteScope: m => m.AccountId == 123);

        // Assert
        var allData = await _fixture.GetAllEntitiesAsync<MetricEntity>();
        Assert.Equal(2, allData.Count); // TOU updated, Wind inserted, Demand and Solar deleted

        // TOU should be updated
        var tou = allData.FirstOrDefault(m => m.Metric == "TOU");
        Assert.NotNull(tou);
        Assert.Equal(999, tou.Value);

        // Wind should be inserted
        var wind = allData.FirstOrDefault(m => m.Metric == "Wind");
        Assert.NotNull(wind);
        Assert.Equal(888, wind.Value);

        // Demand and Solar should be deleted
        Assert.DoesNotContain(allData, m => m.Metric == "Demand");
        Assert.DoesNotContain(allData, m => m.Metric == "Solar");
    }

    [Fact]
    public async Task BulkUpsertWithDeleteScopeAsync_WithCustomMatchOn_ShouldWork()
    {
        // Arrange - Seed existing data
        var baseDate = DateTime.UtcNow.Date;
        var existingData = new List<MetricEntity>
        {
            new() { AccountId = 123, Metric = "TOU", Date = baseDate, Value = 100, Category = "Energy" },
            new() { AccountId = 123, Metric = "Demand", Date = baseDate, Value = 50, Category = "Energy" }
        };
        await _fixture.SeedDataAsync(existingData);

        // Act - Upsert matching on AccountId + Metric + Date (not primary key)
        var upsertData = new List<MetricEntity>
        {
            // Update existing TOU (match on AccountId + Metric + Date)
            new() { AccountId = 123, Metric = "TOU", Date = baseDate, Value = 999, Category = "Updated" },
            // Insert new Solar
            new() { AccountId = 123, Metric = "Solar", Date = baseDate, Value = 888, Category = "Renewable" }
        };

        await using var context = _fixture.CreateNewContext();
        await context.BulkUpsertWithDeleteScopeAsync(
            upsertData,
            matchOn: m => new { m.AccountId, m.Metric, m.Date },
            deleteScope: m => m.AccountId == 123);

        // Assert
        var allData = await _fixture.GetAllEntitiesAsync<MetricEntity>();
        Assert.Equal(2, allData.Count); // TOU updated, Solar inserted, Demand deleted

        // TOU should be updated
        var tou = allData.FirstOrDefault(m => m.Metric == "TOU");
        Assert.NotNull(tou);
        Assert.Equal(999, tou.Value);
        Assert.Equal("Updated", tou.Category);

        // Solar should be inserted
        Assert.Contains(allData, m => m.Metric == "Solar");

        // Demand should be deleted (within deleteScope but not in upsert list)
        Assert.DoesNotContain(allData, m => m.Metric == "Demand");
    }

    [Fact]
    public async Task BulkUpsertWithDeleteScopeAsync_WithCapturedVariables_ShouldWork()
    {
        // Arrange - Seed data
        var existingData = new List<MetricEntity>
        {
            new() { AccountId = 100, Metric = "TOU", Date = DateTime.UtcNow.Date, Value = 1, Category = "Energy" },
            new() { AccountId = 200, Metric = "TOU", Date = DateTime.UtcNow.Date, Value = 2, Category = "Energy" },
            new() { AccountId = 300, Metric = "TOU", Date = DateTime.UtcNow.Date, Value = 3, Category = "Energy" }
        };
        await _fixture.SeedDataAsync(existingData);

        // Act - Use captured variable in deleteScope
        var targetAccountId = 200;
        var targetMetric = "TOU";
        var newData = new List<MetricEntity>
        {
            new() { AccountId = 200, Metric = "Solar", Date = DateTime.UtcNow.Date, Value = 999, Category = "Renewable" }
        };

        await using var context = _fixture.CreateNewContext();
        await context.BulkUpsertWithDeleteScopeAsync(
            newData,
            deleteScope: m => m.AccountId == targetAccountId && m.Metric == targetMetric);

        // Assert
        var allData = await _fixture.GetAllEntitiesAsync<MetricEntity>();
        Assert.Equal(3, allData.Count); // Account 100 and 300 unchanged, Account 200 has Solar only

        // Account 100 and 300 should remain
        Assert.Contains(allData, m => m.AccountId == 100);
        Assert.Contains(allData, m => m.AccountId == 300);

        // Account 200 should only have Solar (TOU deleted)
        var account200 = allData.Where(m => m.AccountId == 200).ToList();
        Assert.Single(account200);
        Assert.Equal("Solar", account200[0].Metric);
    }
}
