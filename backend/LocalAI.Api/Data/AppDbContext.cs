using LocalAI.Api.Models;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

namespace LocalAI.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageFeedback> MessageFeedbacks => Set<MessageFeedback>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("vector");
        modelBuilder.HasPostgresExtension("pg_trgm");

        modelBuilder.Entity<Document>(e =>
        {
            e.HasKey(d => d.Id);
            e.Property(d => d.Embedding).HasColumnType("vector(768)");
            e.HasIndex(d => d.FileName);

            // Parent-child chunking
            e.Property(d => d.ParentChunkId);
            e.Property(d => d.ChunkLevel).HasDefaultValue(0);
            e.HasIndex(d => d.ParentChunkId).HasDatabaseName("idx_document_parent");
            e.HasIndex(d => d.ChunkLevel).HasDatabaseName("idx_document_level");

            // GIN index for trigram search on SearchVector
            e.HasIndex(d => d.SearchVector)
                .HasMethod("GIN")
                .HasOperators("gin_trgm_ops")
                .HasDatabaseName("idx_document_search");

            // GIN index for trigram search on content
            e.HasIndex(d => d.Content)
                .HasMethod("GIN")
                .HasOperators("gin_trgm_ops")
                .HasDatabaseName("idx_document_content_trgm");
        });

        modelBuilder.Entity<Conversation>(e =>
        {
            e.HasKey(c => c.Id);
            e.HasMany(c => c.Messages)
             .WithOne(m => m.Conversation)
             .HasForeignKey(m => m.ConversationId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Message>(e =>
        {
            e.HasKey(m => m.Id);
            e.HasIndex(m => m.ConversationId);
        });

        modelBuilder.Entity<MessageFeedback>(e =>
        {
            e.HasKey(f => f.Id);
            e.HasOne(f => f.Message)
             .WithMany()
             .HasForeignKey(f => f.MessageId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(f => f.MessageId).HasDatabaseName("idx_feedback_message");
        });
    }
}
