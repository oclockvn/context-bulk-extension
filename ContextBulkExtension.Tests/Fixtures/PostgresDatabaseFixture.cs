using ContextBulkExtension.Tests.TestEntities;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace ContextBulkExtension.Tests.Fixtures;

public class PostgresDatabaseFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer? _container;
    private readonly string? _connectionStringOverride;

    public PostgresDatabaseFixture()
    {
        // ponytail: allow env override when Docker not needed
        _connectionStringOverride = Environment.GetEnvironmentVariable("BULK_TEST_PG_CONNECTION");
        if (string.IsNullOrWhiteSpace(_connectionStringOverride))
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .Build();
        }
    }

    public string ConnectionString =>
        _connectionStringOverride
        ?? _container!.GetConnectionString();

    public async Task InitializeAsync()
    {
        if (_container != null)
            await _container.StartAsync();

        await using var context = CreateNewContext();
        await context.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container != null)
            await _container.DisposeAsync();
    }

    public PostgresTestDbContext CreateNewContext()
    {
        var options = new DbContextOptionsBuilder<PostgresTestDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        return new PostgresTestDbContext(options);
    }

    public async Task<List<T>> GetAllEntitiesAsync<T>() where T : class
    {
        await using var context = CreateNewContext();
        return await context.Set<T>().AsNoTracking().ToListAsync();
    }

    public async Task ClearTableAsync<T>() where T : class
    {
        await using var context = CreateNewContext();
        await context.Set<T>().ExecuteDeleteAsync();
    }

    public async Task SeedDataAsync<T>(IEnumerable<T> entities) where T : class
    {
        await using var context = CreateNewContext();
        await context.Set<T>().AddRangeAsync(entities);
        await context.SaveChangesAsync();
    }

    public async Task<int> GetCountAsync<T>() where T : class
    {
        await using var context = CreateNewContext();
        return await context.Set<T>().CountAsync();
    }
}

[CollectionDefinition("PostgresDatabase")]
public class PostgresDatabaseCollection : ICollectionFixture<PostgresDatabaseFixture>
{
}
