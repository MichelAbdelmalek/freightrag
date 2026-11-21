using FreightRag.Core.Infrastructure.Persistence.Records;
using Microsoft.EntityFrameworkCore;

namespace FreightRag.Core.Infrastructure.Persistence;

/// <summary>
/// EF Core model of the FreightRAG schema. Code-first: this model plus the migrations in
/// <c>Infrastructure/Persistence/Migrations</c> are the source of truth for the database schema.
/// Names are configured explicitly (snake_case tables/columns, constraint and index names) so the
/// generated schema is identical to the one originally created by <c>db/001_schema.sql</c>.
/// </summary>
public sealed class FreightRagDbContext(DbContextOptions<FreightRagDbContext> options) : DbContext(options)
{
    /// <summary>nomic-embed-text vectors; must match <see cref="Domain.EmbeddingVector.Dimensions"/>.</summary>
    public const int EmbeddingDimensions = 768;

    /// <summary>PostgreSQL text search configuration: English stemming and stop words.</summary>
    public const string FullTextConfiguration = "english";

    public DbSet<DocumentRecord> Documents => Set<DocumentRecord>();
    public DbSet<ChunkRecord> Chunks => Set<ChunkRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("vector");

        modelBuilder.Entity<DocumentRecord>(document =>
        {
            document.ToTable("documents");
            document.HasKey(d => d.Id).HasName("documents_pkey");
            document.Property(d => d.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            document.Property(d => d.TenantId).HasColumnName("tenant_id");
            document.Property(d => d.Title).HasColumnName("title").HasColumnType("text");
            document.Property(d => d.DocType).HasColumnName("doc_type").HasColumnType("text");
            document.Property(d => d.SourceHash).HasColumnName("source_hash").HasColumnType("text");
            document.Property(d => d.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone")
                .HasDefaultValueSql("now()").ValueGeneratedOnAdd();

            // A UNIQUE constraint (not just a unique index), exactly like the original schema.
            // It is also what makes ingestion idempotent per tenant.
            document.HasAlternateKey(d => new { d.TenantId, d.SourceHash }).HasName("documents_tenant_id_source_hash_key");
        });

        modelBuilder.Entity<ChunkRecord>(chunk =>
        {
            chunk.ToTable("chunks");
            chunk.HasKey(c => c.Id).HasName("chunks_pkey");
            chunk.Property(c => c.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            chunk.Property(c => c.DocumentId).HasColumnName("document_id");
            chunk.Property(c => c.TenantId).HasColumnName("tenant_id");
            chunk.Property(c => c.ChunkIndex).HasColumnName("chunk_index");
            chunk.Property(c => c.Content).HasColumnName("content").HasColumnType("text");
            chunk.Property(c => c.Embedding).HasColumnName("embedding").HasColumnType($"vector({EmbeddingDimensions})");
            chunk.Property(c => c.EmbeddingModel).HasColumnName("embedding_model").HasColumnType("text");

            chunk.HasOne(c => c.Document).WithMany(d => d.Chunks)
                .HasForeignKey(c => c.DocumentId)
                .HasConstraintName("chunks_document_id_fkey")
                .OnDelete(DeleteBehavior.Cascade);

            chunk.HasAlternateKey(c => new { c.DocumentId, c.ChunkIndex }).HasName("chunks_document_id_chunk_index_key");

            // Approximate nearest-neighbour index for cosine distance (the <=> operator).
            chunk.HasIndex(c => c.Embedding).HasDatabaseName("chunks_embedding_hnsw")
                .HasMethod("hnsw").HasOperators("vector_cosine_ops");
            chunk.HasIndex(c => c.TenantId).HasDatabaseName("chunks_tenant");

            // Full-text search: content_tsv = to_tsvector('english', content), GENERATED ... STORED, with a GIN index
            // (an inverted index: lexeme → rows containing it) for the @@ match operator.
            chunk.HasGeneratedTsVectorColumn(c => c.ContentTsv, FullTextConfiguration, c => new { c.Content })
                .HasIndex(c => c.ContentTsv).HasDatabaseName("chunks_content_tsv").HasMethod("GIN");
            chunk.Property(c => c.ContentTsv).HasColumnName("content_tsv");
        });
    }
}
