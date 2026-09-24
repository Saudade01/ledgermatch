using Microsoft.EntityFrameworkCore;

namespace LedgerMatch.Api;

public sealed class ReconciliationDb(DbContextOptions<ReconciliationDb> options) : DbContext(options)
{
    public DbSet<Dataset> Datasets => Set<Dataset>();
    public DbSet<StoredRecord> Records => Set<StoredRecord>();
    public DbSet<ReconciliationRun> Runs => Set<ReconciliationRun>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Dataset>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Source).HasMaxLength(100);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(128);
            entity.Property(x => x.ContentHash).HasMaxLength(64);
            entity.HasIndex(x => x.IdempotencyKey).IsUnique();
            entity.HasMany(x => x.Records).WithOne().HasForeignKey(x => x.DatasetId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<StoredRecord>(entity =>
        {
            entity.HasKey(x => new { x.DatasetId, x.SourceRecordId });
            entity.Property(x => x.SourceRecordId).HasMaxLength(128);
            entity.Property(x => x.Reference).HasMaxLength(128);
            entity.Property(x => x.Currency).HasMaxLength(3);
            entity.HasIndex(x => new { x.DatasetId, x.Reference, x.Currency });
        });
        model.Entity<ReconciliationRun>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ResultJson).HasColumnType("jsonb");
            entity.HasIndex(x => new { x.LeftDatasetId, x.RightDatasetId }).IsUnique();
            entity.HasOne<Dataset>().WithMany().HasForeignKey(x => x.LeftDatasetId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Dataset>().WithMany().HasForeignKey(x => x.RightDatasetId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

public sealed class Dataset
{
    public Guid Id { get; set; }
    public required string Source { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string ContentHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<StoredRecord> Records { get; set; } = [];
}

public sealed class StoredRecord
{
    public Guid DatasetId { get; set; }
    public required string SourceRecordId { get; set; }
    public required string Reference { get; set; }
    public required string Currency { get; set; }
    public long AmountMinor { get; set; }
}

public sealed class ReconciliationRun
{
    public Guid Id { get; set; }
    public Guid LeftDatasetId { get; set; }
    public Guid RightDatasetId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string ResultJson { get; set; }
}
