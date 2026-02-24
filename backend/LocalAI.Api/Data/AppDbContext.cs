using LocalAI.Api.Models;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

namespace LocalAI.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // pgvector extension for semantic search
        modelBuilder.HasPostgresExtension("vector");
        
        // Enable full-text search
        modelBuilder.HasPostgresExtension("pg_trgm"); // trigram for fuzzy matching

        modelBuilder.Entity<Document>(e =>
        {
            e.HasKey(d => d.Id);
            e.Property(d => d.Embedding).HasColumnType("vector(768)");
            e.HasIndex(d => d.FileName);

            // GIN index for full-text search on SearchVector - requires gin_trgm_ops operator class
            e.HasIndex(d => d.SearchVector)
                .HasMethod("GIN")
                .HasOperators("gin_trgm_ops")
                .HasDatabaseName("idx_document_search");

            // GIN index for trigram search (fuzzy matching) - requires gin_trgm_ops operator class
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
    }
}
