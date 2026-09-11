using Microsoft.EntityFrameworkCore;

namespace SharePointToAzureSearch.Core.Data;

/// <summary>
/// Every table the application owns: the worker's delta checkpoints and indexed-file records, and the
/// chat assistant's conversations and messages. The model is the source of truth for the schema — it is
/// what <c>dotnet ef migrations add</c> reads — so table and column names are fixed here rather than
/// configured, and a change to them is a migration.
/// </summary>
public sealed class SharePointIndexDbContext(DbContextOptions<SharePointIndexDbContext> options)
    : DbContext(options)
{
    /// <summary>Identifier columns that carry a Microsoft Graph drive or item ID.</summary>
    private const int IdentifierLength = 200;

    public DbSet<AgentDefinitionEntity> AgentDefinitions => Set<AgentDefinitionEntity>();
    public DbSet<DeltaStateEntity> DeltaState => Set<DeltaStateEntity>();
    public DbSet<IndexedFileEntity> IndexedFiles => Set<IndexedFileEntity>();
    public DbSet<ChatConversationEntity> ChatConversations => Set<ChatConversationEntity>();
    public DbSet<ChatMessageEntity> ChatMessages => Set<ChatMessageEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AgentDefinitionEntity>(entity =>
        {
            entity.ToTable("AgentDefinitions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("NEWSEQUENTIALID()").ValueGeneratedOnAdd();
            entity.Property(x => x.Name).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Instructions).IsRequired();
            entity.Property(x => x.CreatedAtUtc).HasPrecision(7);
            entity.Property(x => x.UpdatedAtUtc).HasPrecision(7);
            entity.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<DeltaStateEntity>(entity =>
        {
            entity.ToTable("SharePointDeltaState");
            entity.HasKey(x => x.DriveId);
            entity.Property(x => x.DriveId).HasMaxLength(IdentifierLength);
            entity.Property(x => x.DeltaLink).IsRequired();
            entity.Property(x => x.UpdatedAtUtc).HasPrecision(7);
        });

        modelBuilder.Entity<IndexedFileEntity>(entity =>
        {
            entity.ToTable("SharePointIndexedFiles");
            entity.HasKey(x => new { x.DriveId, x.ItemId });
            entity.Property(x => x.DriveId).HasMaxLength(IdentifierLength);
            entity.Property(x => x.ItemId).HasMaxLength(IdentifierLength);
            entity.Property(x => x.FileName).HasMaxLength(400).IsRequired();
            entity.Property(x => x.ParentPath).HasMaxLength(1000);
            entity.Property(x => x.WebUrl).HasMaxLength(2000);
            entity.Property(x => x.MimeType).HasMaxLength(200);
            entity.Property(x => x.ETag).HasMaxLength(200);
            entity.Property(x => x.CTag).HasMaxLength(200);
            // A base64 SHA-256 digest: always 44 ASCII characters, so CHAR(44) rather than NCHAR(44).
            entity.Property(x => x.PermissionsHash).HasMaxLength(44).IsFixedLength().IsUnicode(false).IsRequired();
            entity.Property(x => x.IndexFingerprint).HasMaxLength(200).IsRequired();
            entity.Property(x => x.LastModifiedUtc).HasPrecision(7);
            entity.Property(x => x.IndexedAtUtc).HasPrecision(7);

            // The orphan sweep and the operator listing both filter a drive by reconciliation round.
            entity.HasIndex(x => new { x.DriveId, x.ScanId });
        });

        modelBuilder.Entity<ChatConversationEntity>(entity =>
        {
            entity.ToTable("ChatConversations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Title).HasMaxLength(200).IsRequired();
            entity.Property(x => x.UserId).HasMaxLength(200);
            entity.Property(x => x.CreatedAtUtc).HasPrecision(7);
            entity.Property(x => x.UpdatedAtUtc).HasPrecision(7);

            // The sidebar lists conversations most recently used first.
            entity.HasIndex(x => x.UpdatedAtUtc).IsDescending();
        });

        modelBuilder.Entity<ChatMessageEntity>(entity =>
        {
            entity.ToTable("ChatMessages");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Content).IsRequired();
            entity.Property(x => x.CreatedAtUtc).HasPrecision(7);

            // Both enums are stored by name, so a row is readable without the application.
            entity.Property(x => x.Role).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(x => x.Feedback).HasConversion<string>().HasMaxLength(10);

            entity.HasOne(x => x.Conversation)
                .WithMany(x => x.Messages)
                .HasForeignKey(x => x.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);

            // Messages are always read, and appended, in conversation order.
            entity.HasIndex(x => new { x.ConversationId, x.Sequence }).IsUnique();

            // The feedback review page reads only rated messages, newest first.
            entity.HasIndex(x => x.Feedback).HasFilter("[Feedback] IS NOT NULL");
        });
    }
}
