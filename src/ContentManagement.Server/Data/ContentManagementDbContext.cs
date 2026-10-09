using ContentManagement.Server.Domain;
using Microsoft.EntityFrameworkCore;

namespace ContentManagement.Server.Data;

public sealed class ContentManagementDbContext(DbContextOptions<ContentManagementDbContext> options)
    : DbContext(options)
{
    public DbSet<StoredFile> Files => Set<StoredFile>();
    public DbSet<JsonDocumentRecord> JsonDocuments => Set<JsonDocumentRecord>();
    public DbSet<AdminOtpChallenge> AdminOtpChallenges => Set<AdminOtpChallenge>();
    public DbSet<SystemSettings> SystemSettings => Set<SystemSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<SystemSettings>(entity =>
        {
            entity.ToTable("SystemSettings");
            entity.HasKey(settings => settings.Id);
            entity.Property(settings => settings.Id).ValueGeneratedNever();
            entity.HasData(new SystemSettings { Id = 1, SmtpPort = 587, SmtpUseSsl = true, SmtpFromName = "ContentManagement", StaleFileAgeDays = 90, CleanupIntervalHours = 24, DeletionGracePeriodDays = 7, UpdatedUtc = new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc) });
        });

        modelBuilder.Entity<AdminOtpChallenge>(entity =>
        {
            entity.ToTable("AdminOtpChallenges");
            entity.HasKey(challenge => challenge.Id);
            entity.Property(challenge => challenge.Email).HasMaxLength(254).IsRequired();
            entity.Property(challenge => challenge.CodeHash).HasMaxLength(64).IsRequired();
            entity.HasIndex(challenge => new { challenge.Email, challenge.CreatedUtc });
            entity.HasIndex(challenge => new { challenge.Email, challenge.ConsumedUtc, challenge.ExpiresUtc });
        });

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
