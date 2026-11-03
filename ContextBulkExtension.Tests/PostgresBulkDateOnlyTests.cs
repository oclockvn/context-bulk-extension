using ContextBulkExtension.Core;
using ContextBulkExtension.Tests.Fixtures;
using ContextBulkExtension.Tests.TestEntities;
using Microsoft.EntityFrameworkCore;

namespace ContextBulkExtension.Tests;

[Collection("PostgresDatabase")]
public class PostgresBulkDateOnlyTests(PostgresDatabaseFixture fixture) : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _fixture = fixture;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _fixture.ClearTableAsync<DateOnlyEntity>();
    }

    [Fact]
    public async Task BulkInsertAsync_WithDateOnlyAndTimeOnly_ShouldInsertCorrectly()
    {
        var entities = new List<DateOnlyEntity>
        {
            new() { Code = "A", Date = new DateOnly(2024, 2, 29), OptionalDate = new DateOnly(1990, 1, 1), Time = new TimeOnly(8, 30) },
            new() { Code = "B", Date = new DateOnly(2025, 12, 31), OptionalDate = null, Time = new TimeOnly(23, 59, 59) },
        };

        await using var context = _fixture.CreateNewContext();
        await context.BulkInsertAsync(entities, new BulkConfig { IdentityOutput = true });

        Assert.All(entities, e => Assert.True(e.Id > 0));
        var saved = (await _fixture.GetAllEntitiesAsync<DateOnlyEntity>()).OrderBy(e => e.Code).ToList();
        Assert.Equal(new DateOnly(2024, 2, 29), saved[0].Date);
        Assert.Equal(new DateOnly(1990, 1, 1), saved[0].OptionalDate);
        Assert.Equal(new TimeOnly(8, 30), saved[0].Time);
        Assert.Equal(new DateOnly(2025, 12, 31), saved[1].Date);
        Assert.Null(saved[1].OptionalDate);
        Assert.Equal(new TimeOnly(23, 59, 59), saved[1].Time);
    }

    [Fact]
    public async Task BulkUpsertAsync_WithDateOnly_ShouldUpdateMatchedRows()
    {
        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkInsertAsync(new List<DateOnlyEntity>
            {
                new() { Code = "U", Date = new DateOnly(2020, 1, 1), Time = new TimeOnly(1, 0) },
            });
        }

        await using (var context = _fixture.CreateNewContext())
        {
            await context.BulkUpsertAsync(
                new List<DateOnlyEntity>
                {
                    new() { Code = "U", Date = new DateOnly(2021, 6, 15), OptionalDate = new DateOnly(2022, 3, 4), Time = new TimeOnly(2, 0) },
                    new() { Code = "V", Date = new DateOnly(2023, 7, 8), Time = new TimeOnly(3, 0) },
                },
                matchOn: e => e.Code);
        }

        var saved = (await _fixture.GetAllEntitiesAsync<DateOnlyEntity>()).ToDictionary(e => e.Code);
        Assert.Equal(2, saved.Count);
        Assert.Equal(new DateOnly(2021, 6, 15), saved["U"].Date);
        Assert.Equal(new DateOnly(2022, 3, 4), saved["U"].OptionalDate);
        Assert.Equal(new TimeOnly(2, 0), saved["U"].Time);
        Assert.Equal(new DateOnly(2023, 7, 8), saved["V"].Date);
    }
}
