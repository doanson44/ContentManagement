using ContentManagement.Server.Domain;
using Microsoft.EntityFrameworkCore;

namespace ContentManagement.Server.Data;

public sealed class ContentManagementDbContext(DbContextOptions<ContentManagementDbContext> options)
    : DbContext(options)
{
    public DbSet<StoredFile> Files => Set<StoredFile>();
    public DbSet<JsonDocumentRecord> JsonDocuments => Set<JsonDocumentRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<StoredFile>(entity =>
        {
            entity.ToTable("Files");
            entity.HasKey(file => file.Id);
            entity.Property(file => file.Id).ValueGeneratedNever();
            entity.Property(file => file.StorageKey).IsRequired();
            entity.HasIndex(file => file.StorageKey).IsUnique();
            entity.HasIndex(file => new { file.Status, file.CreatedUtc });
            entity.HasIndex(file => file.OwnerId);
            entity.Property(file => file.SizeBytes).HasColumnType("bigint");
            entity.Property(file => file.Status).HasConversion<int>();
            entity.Property(file => file.RowVersion).IsRowVersion();
        });

        modelBuilder.Entity<JsonDocumentRecord>(entity =>
        {
            entity.ToTable("JsonDocuments");
            entity.HasKey(document => document.Id);
            entity.Property(document => document.Id).ValueGeneratedNever();
            entity.Property(document => document.CompressedPayload).HasColumnType("varbinary(max)").IsRequired();
            entity.Property(document => document.UncompressedSizeBytes).HasColumnType("bigint");
            entity.Property(document => document.CompressedSizeBytes).HasColumnType("bigint");
            entity.Property(document => document.Status).HasConversion<int>();
            entity.HasIndex(document => new { document.DocumentType, document.CreatedUtc });
            entity.HasIndex(document => new { document.Status, document.UpdatedUtc });
            entity.HasIndex(document => document.OwnerId);
            entity.Property(document => document.RowVersion).IsRowVersion();
        });
    }
}
