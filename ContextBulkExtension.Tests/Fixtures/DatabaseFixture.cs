using ContextBulkExtension.Tests.TestEntities;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;

namespace ContextBulkExtension.Tests.Fixtures;

public class DatabaseFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container;
    private TestDbContext? _context;

    public DatabaseFixture()
    {
        _container = new MsSqlBuilder()
            .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
            .Build();
    }

    public TestDbContext Context => _context ?? throw new InvalidOperationException("Database not initialized");
    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlServer(_container.GetConnectionString())
            .Options;

        _context = new TestDbContext(options);
        await _context.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        if (_context != null)
        {
            await _context.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    public async Task<List<T>> GetAllEntitiesAsync<T>() where T : class
    {
        return await Context.Set<T>().ToListAsync();
    }

    public async Task ClearTableAsync<T>() where T : class
    {
        Context.Set<T>().RemoveRange(Context.Set<T>());
        await Context.SaveChangesAsync();
    }

    public async Task SeedDataAsync<T>(IEnumerable<T> entities) where T : class
    {
        await Context.Set<T>().AddRangeAsync(entities);
        await Context.SaveChangesAsync();
    }

    public async Task ExecuteInTransactionAsync(Func<Task> action)
    {
        await using var transaction = await Context.Database.BeginTransactionAsync();
        try
        {
            await action();
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
        return await Context.Set<T>().CountAsync();
    }
}
