using ContentManagement.Server.Data;
using ContentManagement.Server.Domain;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

#nullable disable

namespace ContentManagement.Server.Migrations;

[DbContext(typeof(ContentManagementDbContext))]
partial class ContentManagementDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder
            .HasAnnotation("ProductVersion", "10.0.0")
            .HasAnnotation("Relational:MaxIdentifierLength", 128);

        modelBuilder.Entity("Microsoft.AspNetCore.DataProtection.EntityFrameworkCore.DataProtectionKey", b =>
        {
            b.Property<int>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("int")
                .HasAnnotation("SqlServer:ValueGenerationStrategy", SqlServerValueGenerationStrategy.IdentityColumn);
            b.Property<string>("FriendlyName").HasColumnType("nvarchar(max)");
            b.Property<string>("Xml").HasColumnType("nvarchar(max)");
            b.HasKey("Id");
            b.ToTable("DataProtectionKeys");
        });

        modelBuilder.Entity("ContentManagement.Server.Domain.AdminOtpChallenge", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever().HasColumnType("uniqueidentifier");
            b.Property<string>("CodeHash").IsRequired().HasMaxLength(64).HasColumnType("nvarchar(64)");
            b.Property<DateTime>("CreatedUtc").HasColumnType("datetime2");
            b.Property<DateTime?>("ConsumedUtc").HasColumnType("datetime2");
            b.Property<string>("Email").IsRequired().HasMaxLength(254).HasColumnType("nvarchar(254)");
            b.Property<DateTime>("ExpiresUtc").HasColumnType("datetime2");
            b.Property<int>("FailedAttempts").HasColumnType("int");
            b.HasKey("Id");
            b.HasIndex("Email", "CreatedUtc");
            b.HasIndex("Email", "ConsumedUtc", "ExpiresUtc");
            b.ToTable("AdminOtpChallenges");
        });

        modelBuilder.Entity("ContentManagement.Server.Domain.ApiClient", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever().HasColumnType("uniqueidentifier");
            b.Property<bool>("IsActive").HasColumnType("bit");
            b.Property<string>("ClientId").IsRequired().HasMaxLength(32).HasColumnType("nvarchar(32)");
            b.Property<DateTime>("CreatedUtc").HasColumnType("datetime2");
            b.Property<string>("Name").IsRequired().HasMaxLength(100).HasColumnType("nvarchar(100)");
            b.Property<string>("ScopesJson").IsRequired().HasMaxLength(1000).HasColumnType("nvarchar(1000)");
            b.Property<string>("SecretHash").IsRequired().HasMaxLength(64).HasColumnType("nvarchar(64)");
            b.Property<DateTime>("UpdatedUtc").HasColumnType("datetime2");
            b.Property<byte[]>("RowVersion").IsRequired().IsConcurrencyToken().ValueGeneratedOnAddOrUpdate().HasColumnType("rowversion");
            b.HasKey("Id");
            b.HasIndex("ClientId").IsUnique();
            b.HasIndex("IsActive", "Name");
            b.ToTable("ApiClients");
        });

        modelBuilder.Entity("ContentManagement.Server.Domain.PublicHtmlPage", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever().HasColumnType("uniqueidentifier");
            b.Property<DateTime>("CreatedUtc").HasColumnType("datetime2");
            b.Property<bool>("IsDeleted").ValueGeneratedOnAdd().HasColumnType("bit").HasDefaultValue(false);
            b.Property<bool>("IsPublished").ValueGeneratedOnAdd().HasColumnType("bit").HasDefaultValue(false);
            b.Property<DateTime?>("LastViewedAtUtc").HasColumnType("datetime2");
            b.Property<string>("OwnerId").HasMaxLength(200).HasColumnType("nvarchar(200)");
            b.Property<DateTime?>("PublishedAtUtc").HasColumnType("datetime2");
            b.Property<string>("Sha256").IsRequired().HasMaxLength(64).HasColumnType("nvarchar(64)");
            b.Property<long>("SizeBytes").HasColumnType("bigint");
            b.Property<string>("Slug").IsRequired().HasMaxLength(80).HasColumnType("nvarchar(80)");
            b.Property<string>("StorageKey").IsRequired().HasMaxLength(180).HasColumnType("nvarchar(180)");
            b.Property<bool>("StorageDeletePending").ValueGeneratedOnAdd().HasColumnType("bit").HasDefaultValue(false);
            b.Property<string>("Title").IsRequired().HasMaxLength(256).HasColumnType("nvarchar(256)");
            b.Property<DateTime>("UpdatedUtc").HasColumnType("datetime2");
            b.HasKey("Id");
            b.HasIndex("LastViewedAtUtc");
            b.HasIndex("Slug").IsUnique();
            b.HasIndex("StorageDeletePending");
            b.HasIndex("IsPublished", "IsDeleted", "CreatedUtc");
            b.ToTable("PublicHtmlPages");
        });

        modelBuilder.Entity("ContentManagement.Server.Domain.QuickNote", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever().HasColumnType("uniqueidentifier");
            b.Property<string>("Content").IsRequired().HasMaxLength(20000).HasColumnType("nvarchar(max)");
            b.Property<DateTime>("CreatedUtc").HasColumnType("datetime2");
            b.Property<bool>("IsPinned").HasColumnType("bit");
            b.Property<string>("Title").IsRequired().HasMaxLength(200).HasColumnType("nvarchar(200)");
            b.Property<DateTime>("UpdatedUtc").HasColumnType("datetime2");
            b.Property<byte[]>("RowVersion").IsRequired().IsConcurrencyToken().ValueGeneratedOnAddOrUpdate().HasColumnType("rowversion");
            b.HasKey("Id");
            b.HasIndex("IsPinned", "UpdatedUtc");
            b.ToTable("QuickNotes");
        });

        modelBuilder.Entity("ContentManagement.Server.Domain.StoredFile", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever().HasColumnType("uniqueidentifier");
            b.Property<string>("ContentType").IsRequired().HasMaxLength(255).HasColumnType("nvarchar(255)");
            b.Property<DateTime>("CreatedUtc").HasColumnType("datetime2");
            b.Property<string>("FileName").IsRequired().HasMaxLength(255).HasColumnType("nvarchar(255)");
            b.Property<string>("OwnerId").HasMaxLength(200).HasColumnType("nvarchar(200)");
            b.Property<byte[]>("RowVersion").IsRequired().IsConcurrencyToken().ValueGeneratedOnAddOrUpdate().HasColumnType("rowversion");
            b.Property<string>("Sha256").IsRequired().HasMaxLength(64).HasColumnType("nvarchar(64)");
            b.Property<long>("SizeBytes").HasColumnType("bigint");
            b.Property<ContentStatus>("Status").HasConversion<string>().HasMaxLength(32).IsRequired().HasColumnType("nvarchar(32)");
            b.Property<string>("StorageKey").IsRequired().HasMaxLength(180).HasColumnType("nvarchar(180)");
            b.Property<FileUploadSource>("UploadSource").ValueGeneratedOnAdd().HasConversion<string>().HasMaxLength(32).IsRequired().HasColumnType("nvarchar(32)").HasDefaultValue(FileUploadSource.Local);
            b.Property<DateTime>("UpdatedUtc").HasColumnType("datetime2");
            b.HasKey("Id");
            b.HasIndex("OwnerId");
            b.HasIndex("StorageKey").IsUnique();
            b.HasIndex("Status", "CreatedUtc");
            b.ToTable("Files");
        });

        modelBuilder.Entity("ContentManagement.Server.Domain.JsonDocumentRecord", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever().HasColumnType("uniqueidentifier");
            b.Property<string>("Code").IsRequired().HasMaxLength(100).HasColumnType("nvarchar(100)");
            b.Property<long>("CompressedSizeBytes").HasColumnType("bigint");
            b.Property<byte[]>("CompressedPayload").IsRequired().HasColumnType("varbinary(max)");
            b.Property<DateTime>("CreatedUtc").HasColumnType("datetime2");
            b.Property<string>("DocumentType").IsRequired().HasMaxLength(200).HasColumnType("nvarchar(200)");
            b.Property<string>("OwnerId").HasMaxLength(200).HasColumnType("nvarchar(200)");
            b.Property<byte[]>("RowVersion").IsRequired().IsConcurrencyToken().ValueGeneratedOnAddOrUpdate().HasColumnType("rowversion");
            b.Property<string>("Sha256").IsRequired().HasMaxLength(64).HasColumnType("nvarchar(64)");
            b.Property<long>("UncompressedSizeBytes").HasColumnType("bigint");
            b.Property<ContentStatus>("Status").HasConversion<string>().HasMaxLength(32).IsRequired().HasColumnType("nvarchar(32)");
            b.Property<DateTime>("UpdatedUtc").HasColumnType("datetime2");
            b.HasKey("Id");
            b.HasIndex("Code").IsUnique();
            b.HasIndex("OwnerId");
            b.HasIndex("DocumentType", "CreatedUtc");
            b.HasIndex("Status", "UpdatedUtc");
            b.ToTable("JsonDocuments");
        });

        modelBuilder.Entity("ContentManagement.Server.Domain.SystemSettings", b =>
        {
            b.Property<int>("Id").ValueGeneratedNever().HasColumnType("int");
            b.Property<long>("ExternalStorageQuotaBytes").HasColumnType("bigint");
            b.Property<bool>("ExternalStoragePreferred").HasColumnType("bit");
            b.Property<int>("StaleFileAgeDays").HasColumnType("int");
            b.HasKey("Id");
            b.ToTable("SystemSettings");
            b.HasData(
                new
                {
                    Id = 1,
                    ExternalStoragePreferred = false,
                    ExternalStorageQuotaBytes = 10737418240L,
                    StaleFileAgeDays = 90
                });
        });
    }
}
