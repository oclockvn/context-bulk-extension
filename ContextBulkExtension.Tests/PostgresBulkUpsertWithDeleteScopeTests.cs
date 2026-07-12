using ContextBulkExtension.Tests.Fixtures;
using ContextBulkExtension.Tests.TestEntities;

namespace ContextBulkExtension.Tests;

[Collection("PostgresDatabase")]
public class PostgresBulkUpsertWithDeleteScopeTests(PostgresDatabaseFixture fixture) : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _fixture = fixture;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _fixture.ClearTableAsync<MetricEntity>();
    }

    [Fact]
    public async Task BulkUpsertWithDeleteScopeAsync_ShouldInsertUpdateAndDelete()
    {
        await _fixture.SeedDataAsync([
            new MetricEntity { AccountId = 1, Metric = "A", Date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), Value = 1, Category = "keep" },
            new MetricEntity { AccountId = 1, Metric = "B", Date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), Value = 2, Category = "delete-me" },
            new MetricEntity { AccountId = 2, Metric = "C", Date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), Value = 3, Category = "other-account" }
        ]);

        var existing = await _fixture.GetAllEntitiesAsync<MetricEntity>();
        var keep = existing.Single(e => e.Metric == "A");

        var batch = new List<MetricEntity>
        {
            new()
            {
                Id = keep.Id,
                AccountId = 1,
                Metric = "A",
                Date = keep.Date,
                Value = 100,
                Category = "keep"
            },
            new()
            {
                AccountId = 1,
                Metric = "D",
                Date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Value = 4,
                Category = "new"
            }
        };

        await using var context = _fixture.CreateNewContext();
        await context.BulkUpsertWithDeleteScopeAsync(
            batch,
            matchOn: x => new { x.AccountId, x.Metric, x.Date },
            deleteScope: x => x.AccountId == 1);

        var all = await _fixture.GetAllEntitiesAsync<MetricEntity>();
        Assert.Equal(3, all.Count);
        Assert.Contains(all, e => e.Metric == "A" && e.Value == 100);
        Assert.Contains(all, e => e.Metric == "D" && e.Value == 4);
        Assert.Contains(all, e => e.AccountId == 2 && e.Metric == "C");
        Assert.DoesNotContain(all, e => e.Metric == "B");
    }
}
