using Microsoft.EntityFrameworkCore;

namespace ContextBulkExtension.Tests.TestEntities;

public class TestDbContext : DbContext
{
    public TestDbContext(DbContextOptions<TestDbContext> options) : base(options)
    {
    }

    public DbSet<SimpleEntity> SimpleEntities { get; set; }
    public DbSet<CompositeKeyEntity> CompositeKeyEntities { get; set; }
    public DbSet<EntityWithoutIdentity> EntitiesWithoutIdentity { get; set; }
    public DbSet<EntityWithComputedColumn> EntitiesWithComputedColumn { get; set; }
    public DbSet<UserEntity> UserEntities { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // SimpleEntity configuration
        modelBuilder.Entity<SimpleEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Value).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
        });

        // CompositeKeyEntity configuration
        modelBuilder.Entity<CompositeKeyEntity>(entity =>
        {
            entity.HasKey(e => new { e.Key1, e.Key2 });
            entity.Property(e => e.Key2).HasMaxLength(100);
            entity.Property(e => e.Data).HasMaxLength(500);
            entity.Property(e => e.Counter).IsRequired();
        });

        // EntityWithoutIdentity configuration
        modelBuilder.Entity<EntityWithoutIdentity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Code).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Amount).HasPrecision(18, 2);
        });

        // EntityWithComputedColumn configuration
        modelBuilder.Entity<EntityWithComputedColumn>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.FirstName).HasMaxLength(100).IsRequired();
            entity.Property(e => e.LastName).HasMaxLength(100).IsRequired();
            entity.Property(e => e.FullName)
                .HasMaxLength(201)
                .HasComputedColumnSql("[FirstName] + ' ' + [LastName]", stored: true);
            entity.Property(e => e.UpdatedAt)
                .ValueGeneratedOnAddOrUpdate()
                .HasDefaultValueSql("GETUTCDATE()");
        });

        // UserEntity configuration
        modelBuilder.Entity<UserEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Email).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Username).HasMaxLength(100).IsRequired();
            entity.Property(e => e.FirstName).HasMaxLength(100).IsRequired();
            entity.Property(e => e.LastName).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Points).IsRequired();
            entity.Property(e => e.IsActive).IsRequired();
            entity.Property(e => e.RegisteredAt).IsRequired();

            // Add unique index on Email for custom matchOn scenarios
            entity.HasIndex(e => e.Email).IsUnique();
            // Add composite index on Email + Username for composite matchOn tests
            entity.HasIndex(e => new { e.Email, e.Username }).IsUnique();
        });
    }
}
