// QuickStart — a runnable end-to-end exercise of every public entry point.
//
//   dotnet run --project samples/QuickStart
//
// Requires a running Docker daemon (a throwaway PostgreSQL container is started
// via Testcontainers). Set QUICKSTART_PG_CONNECTION to use an existing database
// instead. Exit code 0 means every assertion passed.

using ContextBulkExtension.Core;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

var overrideConn = Environment.GetEnvironmentVariable("QUICKSTART_PG_CONNECTION");

PostgreSqlContainer? container = null;
string connectionString;

if (string.IsNullOrWhiteSpace(overrideConn))
{
    Console.WriteLine("Starting postgres:16-alpine container (first run pulls the image)...");
    container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
    await container.StartAsync();
    connectionString = container.GetConnectionString();
}
else
{
    connectionString = overrideConn;
}

try
{
    var options = new DbContextOptionsBuilder<ShopContext>()
        .UseNpgsql(connectionString)
        .Options;

    await using (var db = new ShopContext(options))
        await db.Database.EnsureCreatedAsync();

    // ---------------------------------------------------------------------
    // 1. BulkInsertAsync — raw, fast load. No keys returned.
    // ---------------------------------------------------------------------
    await using (var db = new ShopContext(options))
    {
        var products = Enumerable.Range(1, 5_000)
            .Select(i => new Product { Sku = $"SKU-{i:D5}", Name = $"Product {i}", Price = i % 97 })
            .ToList();

        await db.BulkInsertAsync(products);
        var count = await db.Products.CountAsync();
        Assert(count == 5_000, $"BulkInsertAsync: expected 5000 rows, got {count}");
        Console.WriteLine($"1. BulkInsertAsync      -> inserted {count} products");
    }

    // ---------------------------------------------------------------------
    // 2. BulkInsertAsync with IdentityOutput — keys written back to entities.
    // ---------------------------------------------------------------------
    await using (var db = new ShopContext(options))
    {
        var extra = new List<Product>
        {
            new() { Sku = "SKU-90001", Name = "Keyed A", Price = 10 },
            new() { Sku = "SKU-90002", Name = "Keyed B", Price = 20 },
        };

        await db.BulkInsertAsync(extra, new BulkConfig { IdentityOutput = true });
        Assert(extra.All(p => p.Id > 0), "IdentityOutput: entities should have generated Ids");
        Console.WriteLine($"2. BulkInsert+Identity  -> generated Ids {string.Join(", ", extra.Select(p => p.Id))}");
    }

    // ---------------------------------------------------------------------
    // 3. BulkUpsertAsync — match on the unique Sku, update Price + Name.
    // ---------------------------------------------------------------------
    await using (var db = new ShopContext(options))
    {
        var changes = new List<Product>
        {
            new() { Sku = "SKU-00001", Name = "Product 1 (updated)", Price = 999 }, // existing -> update
            new() { Sku = "SKU-99999", Name = "Brand new",           Price = 1   }, // missing  -> insert
        };

        await db.BulkUpsertAsync(
            changes,
            matchOn: p => p.Sku,
            updateColumns: p => new { p.Price, p.Name });

        var updated = await db.Products.SingleAsync(p => p.Sku == "SKU-00001");
        var inserted = await db.Products.SingleAsync(p => p.Sku == "SKU-99999");
        Assert(updated.Price == 999m, $"Upsert update: expected price 999, got {updated.Price}");
        Assert(inserted.Name == "Brand new", "Upsert insert: new row missing");
        Console.WriteLine("3. BulkUpsertAsync      -> 1 updated, 1 inserted (matched on Sku)");
    }

    // ---------------------------------------------------------------------
    // 4. BulkUpsertWithDeleteScopeAsync — reconcile one category to a batch.
    //    deleteScope limits deletions to Category == "clearance"; rows in
    //    that category not present in the batch are removed.
    // ---------------------------------------------------------------------
    await using (var db = new ShopContext(options))
    {
        await db.BulkInsertAsync(new List<Product>
        {
            new() { Sku = "CLR-1", Name = "Clearance 1", Price = 5, Category = "clearance" },
            new() { Sku = "CLR-2", Name = "Clearance 2", Price = 5, Category = "clearance" },
            new() { Sku = "CLR-3", Name = "Clearance 3", Price = 5, Category = "clearance" },
        });

        var desired = new List<Product>
        {
            new() { Sku = "CLR-1", Name = "Clearance 1", Price = 4, Category = "clearance" }, // keep + update
            new() { Sku = "CLR-4", Name = "Clearance 4", Price = 3, Category = "clearance" }, // insert
            // CLR-2 and CLR-3 are absent -> deleted (they are in scope)
        };

        await db.BulkUpsertWithDeleteScopeAsync(
            desired,
            matchOn: p => p.Sku,
            deleteScope: p => p.Category == "clearance");

        var remaining = await db.Products
            .Where(p => p.Category == "clearance")
            .Select(p => p.Sku)
            .OrderBy(s => s)
            .ToListAsync();

        Assert(remaining.SequenceEqual(new[] { "CLR-1", "CLR-4" }),
            $"DeleteScope: expected [CLR-1, CLR-4], got [{string.Join(", ", remaining)}]");
        Console.WriteLine("4. UpsertWithDeleteScope-> clearance reconciled to [CLR-1, CLR-4]");
    }

    Console.WriteLine("\nAll QuickStart assertions passed.");
    return 0;
}
finally
{
    if (container is not null)
        await container.DisposeAsync();
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException("ASSERTION FAILED: " + message);
}

// ---------------------------------------------------------------------------

public class Product
{
    public int Id { get; set; }
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public string? Category { get; set; }
}

public class ShopContext(DbContextOptions<ShopContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).ValueGeneratedOnAdd();
            e.Property(p => p.Sku).HasMaxLength(20).IsRequired();
            e.Property(p => p.Name).HasMaxLength(200).IsRequired();
            e.Property(p => p.Price).HasPrecision(18, 2);
            e.Property(p => p.Category).HasMaxLength(50);
            // Unique index required for matchOn: p => p.Sku on PostgreSQL.
            e.HasIndex(p => p.Sku).IsUnique();
        });
    }
}
