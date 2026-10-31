using ContentManagement.Server.Domain;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ContentManagement.Server.Data;

public sealed class ContentManagementDbContext(DbContextOptions<ContentManagementDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<StoredFile> Files => Set<StoredFile>();
    public DbSet<JsonDocumentRecord> JsonDocuments => Set<JsonDocumentRecord>();
    public DbSet<AdminOtpChallenge> AdminOtpChallenges => Set<AdminOtpChallenge>();
    public DbSet<SystemSettings> SystemSettings => Set<SystemSettings>();
    public DbSet<ApiClient> ApiClients => Set<ApiClient>();
    public DbSet<QuickNote> QuickNotes => Set<QuickNote>();
    public DbSet<PinnedProxy> PinnedProxies => Set<PinnedProxy>();
    public DbSet<PublicHtmlPage> PublicHtmlPages => Set<PublicHtmlPage>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Persist every enum property by its stable symbolic name, not its underlying numeric value.
        foreach (var property in modelBuilder.Model.GetEntityTypes()
                     .SelectMany(entityType => entityType.GetProperties())
                     .Where(property => (Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType).IsEnum))
        {
            var enumType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
            var converterType = typeof(EnumToStringConverter<>).MakeGenericType(enumType);
            property.SetValueConverter((ValueConverter)Activator.CreateInstance(converterType)!);
            property.SetMaxLength(32);
        }

        modelBuilder.Entity<DataProtectionKey>(entity =>
        {
            entity.ToTable("DataProtectionKeys");
            entity.HasKey(key => key.Id);
        });

        modelBuilder.Entity<SystemSettings>(entity =>
        {
            entity.ToTable("SystemSettings");
            entity.HasKey(settings => settings.Id);
            entity.Property(settings => settings.Id).ValueGeneratedNever();
            entity.HasData(new SystemSettings { Id = 1, StaleFileAgeDays = 90 });
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

        modelBuilder.Entity<ApiClient>(entity =>
        {
            entity.ToTable("ApiClients");
            entity.HasKey(client => client.Id);
            entity.Property(client => client.Id).ValueGeneratedNever();
            entity.Property(client => client.ClientId).HasMaxLength(32).IsRequired();
            entity.HasIndex(client => client.ClientId).IsUnique();
            entity.Property(client => client.Name).HasMaxLength(100).IsRequired();
            entity.Property(client => client.SecretHash).HasMaxLength(64).IsRequired();
            entity.Property(client => client.ScopesJson).HasMaxLength(1000).IsRequired();
            entity.HasIndex(client => new { client.IsActive, client.Name });
            entity.Property(client => client.RowVersion).IsRowVersion();
        });

        modelBuilder.Entity<PublicHtmlPage>(entity =>
        {
            entity.ToTable("PublicHtmlPages");
            entity.HasKey(page => page.Id);
            entity.Property(page => page.Id).ValueGeneratedNever();
            entity.Property(page => page.Title).HasMaxLength(256).IsRequired();
            entity.Property(page => page.Slug).HasMaxLength(80).IsRequired();
            entity.HasIndex(page => page.Slug).IsUnique();
            entity.Property(page => page.StorageKey).HasMaxLength(180).IsRequired();
            entity.Property(page => page.SizeBytes).HasColumnType("bigint");
            entity.Property(page => page.Sha256).HasMaxLength(64).IsRequired();
            entity.Property(page => page.OwnerId).HasMaxLength(200);
            entity.Property(page => page.IsPublished).HasDefaultValue(false);
            entity.Property(page => page.IsDeleted).HasDefaultValue(false);
            entity.Property(page => page.StorageDeletePending).HasDefaultValue(false);
            entity.HasIndex(page => new { page.IsPublished, page.IsDeleted, page.CreatedUtc });
            entity.HasIndex(page => page.LastViewedAtUtc);
            entity.HasIndex(page => page.StorageDeletePending);
        });

        modelBuilder.Entity<PinnedProxy>(entity =>
        {
            entity.ToTable("PinnedProxies");
            entity.HasKey(proxy => proxy.Id);
            entity.Property(proxy => proxy.Id).ValueGeneratedNever();
            entity.Property(proxy => proxy.Address).HasMaxLength(256).IsRequired();
            entity.Property(proxy => proxy.Protocol).HasMaxLength(16).IsRequired();
            entity.HasIndex(proxy => proxy.PinnedUtc);
        });

        modelBuilder.Entity<QuickNote>(entity =>
        {
            entity.ToTable("QuickNotes");
            entity.HasKey(note => note.Id);
            entity.Property(note => note.Id).ValueGeneratedNever();
            entity.Property(note => note.Title).HasMaxLength(200).IsRequired();
            entity.Property(note => note.Content).HasMaxLength(20000).IsRequired();
            entity.HasIndex(note => new { note.IsPinned, note.UpdatedUtc });
            entity.Property(note => note.RowVersion).IsRowVersion();
        });

        modelBuilder.Entity<StoredFile>(entity =>
        {
            entity.ToTable("Files");
            entity.HasKey(file => file.Id);
            entity.Property(file => file.Id).ValueGeneratedNever();
            entity.Property(file => file.StorageKey).IsRequired();
            entity.Property(file => file.FilePath).HasMaxLength(450).IsRequired();
            entity.HasIndex(file => file.FilePath).IsUnique();
            entity.HasIndex(file => file.StorageKey).IsUnique();
            entity.HasIndex(file => new { file.Status, file.CreatedUtc });
            entity.HasIndex(file => file.OwnerId);
            entity.Property(file => file.Type).HasConversion<string>().HasMaxLength(32)
                .HasDefaultValue(FileOwnerType.Legacy);
            entity.Property(file => file.SizeBytes).HasColumnType("bigint");
            entity.Property(file => file.UploadSource).HasDefaultValue(FileUploadSource.Local);
            entity.Property(file => file.RowVersion).IsRowVersion();
        });

        modelBuilder.Entity<JsonDocumentRecord>(entity =>
        {
            entity.ToTable("JsonDocuments");
            entity.HasKey(document => document.Id);
            entity.Property(document => document.Id).ValueGeneratedNever();
            entity.Property(document => document.Code).HasMaxLength(100).IsRequired();
            entity.HasIndex(document => document.Code).IsUnique();
            entity.Property(document => document.CompressedPayload).HasColumnType("varbinary(max)").IsRequired();
            entity.Property(document => document.UncompressedSizeBytes).HasColumnType("bigint");
            entity.Property(document => document.CompressedSizeBytes).HasColumnType("bigint");
            entity.HasIndex(document => new { document.DocumentType, document.CreatedUtc });
            entity.HasIndex(document => new { document.Status, document.UpdatedUtc });
            entity.HasIndex(document => document.OwnerId);
            entity.Property(document => document.RowVersion).IsRowVersion();
        });
    }
}
