using ContextBulkExtension.Tests.TestEntities;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;

namespace ContextBulkExtension.Tests.Fixtures;

public class DatabaseFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container;

    public DatabaseFixture()
    {
        _container = new MsSqlBuilder()
            .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
            .Build();
    }

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Create a context just to ensure database schema is created
        await using var context = CreateNewContext();
        await context.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    public TestDbContext CreateNewContext()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlServer(_container.GetConnectionString())
            .Options;

        return new TestDbContext(options);
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

    public async Task ExecuteInTransactionAsync(Func<TestDbContext, Task> action)
    {
        await using var context = CreateNewContext();
        await using var transaction = await context.Database.BeginTransactionAsync();
        try
        {
            await action(context);
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<int> GetCountAsync<T>() where T : class
    {
        await using var context = CreateNewContext();
        return await context.Set<T>().CountAsync();
    }
}
