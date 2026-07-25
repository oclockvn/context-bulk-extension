using Microsoft.EntityFrameworkCore;

namespace ContextBulkExtension.Tests.TestEntities;

/// <summary>
/// Postgres-flavored model (computed/default SQL differs from SQL Server TestDbContext).
/// </summary>
public class PostgresTestDbContext : DbContext
{
    public PostgresTestDbContext(DbContextOptions<PostgresTestDbContext> options) : base(options)
    {
    }

    public DbSet<SimpleEntity> SimpleEntities { get; set; }
    public DbSet<CompositeKeyEntity> CompositeKeyEntities { get; set; }
    public DbSet<EntityWithoutIdentity> EntitiesWithoutIdentity { get; set; }
    public DbSet<EntityWithComputedColumn> EntitiesWithComputedColumn { get; set; }
    public DbSet<UserEntity> UserEntities { get; set; }
    public DbSet<MetricEntity> MetricEntities { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<SimpleEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Value).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
        });

        modelBuilder.Entity<CompositeKeyEntity>(entity =>
        {
            entity.HasKey(e => new { e.Key1, e.Key2 });
            entity.Property(e => e.Key2).HasMaxLength(100);
            entity.Property(e => e.Data).HasMaxLength(500);
            entity.Property(e => e.Counter).IsRequired();
        });

        modelBuilder.Entity<EntityWithoutIdentity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Code).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Amount).HasPrecision(18, 2);
        });

        modelBuilder.Entity<EntityWithComputedColumn>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.FirstName).HasMaxLength(100).IsRequired();
            entity.Property(e => e.LastName).HasMaxLength(100).IsRequired();
            entity.Property(e => e.FullName)
                .HasMaxLength(201)
                .HasComputedColumnSql("\"FirstName\" || ' ' || \"LastName\"", stored: true);
            entity.Property(e => e.UpdatedAt)
                .ValueGeneratedOnAddOrUpdate()
                .HasDefaultValueSql("now() at time zone 'utc'");
        });

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

            entity.HasIndex(e => e.Email).IsUnique();
            entity.HasIndex(e => new { e.Email, e.Username }).IsUnique();
        });

        modelBuilder.Entity<MetricEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.AccountId).IsRequired();
            entity.Property(e => e.Metric).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Date).IsRequired();
            entity.Property(e => e.Value).HasPrecision(18, 4).IsRequired();
            entity.Property(e => e.Category).HasMaxLength(100);
        });
    }
}
