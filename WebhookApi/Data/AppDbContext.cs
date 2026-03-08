using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using RedwoodIloilo.Common.Entities;

namespace WebhookApi.Data
{
    public class AppDbContext : DbContext
    {
        private readonly IConfiguration _configuration;

        public AppDbContext(DbContextOptions<AppDbContext> options, IConfiguration configuration)
            : base(options)
        {
            _configuration = configuration;

            // Explicitly assign DbSet properties so the non-nullable analyzer is satisfied.
            GuestMessages = Set<GuestMessage>();
            GuestResponses = Set<GuestResponse>();
            Configs = Set<Config>();
            Rules = Set<Rule>();
            RuleCategories = Set<RuleCategory>();
            GuestAssessments = Set<GuestAssessment>();
            GuestPayments = Set<GuestPayment>();
            // RAG-related sets (types come from the RedwoodIloilo.Common.Entities NuGet package)
            RagDocuments = Set<RagDocument>();
            RagChunks = Set<RagChunk>();
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // The `RagChunk` type (from RedwoodIloilo.Common.Entities) may expose an `Embedding` property
            // of type `Pgvector.Vector` which EF Core cannot bind because the type has no mappable
            // constructor parameters. We ignore this property so EF won't try to map it as an entity
            // property; embeddings are already stored in `MetadataJson` as a fallback.
            try
            {
                var ragChunkType = typeof(RagChunk);
                modelBuilder.Entity(ragChunkType).Ignore("Embedding");
            }
            catch
            {
                // Swallow any error here to avoid crashing model creation if the type isn't present
                // in some environments (e.g., trimmed builds). Prefer logging at higher levels.
            }

            // If the compiled `RagDocument` type exposes a CLR `MetadataJson` property
            // as `string`, ignore that CLR property and instead add a uniquely-named
            // shadow property to represent the JSON document. Map the shadow property
            // to the actual DB column name `MetadataJson` and set its column type to
            // `jsonb` so Npgsql will transmit jsonb-typed parameters.
            try
            {
                modelBuilder.Entity(typeof(RagDocument)).Ignore("MetadataJson");
            }
            catch
            {
                // ignore if ignore fails
            }

            try
            {
                // Use a distinct shadow property name to avoid colliding with the CLR property
                const string shadowName = "_MetadataJsonShadow";
                modelBuilder.Entity(typeof(RagDocument))
                    .Property(typeof(System.Text.Json.JsonDocument), shadowName)
                    .HasColumnName("MetadataJson")
                    .HasColumnType("jsonb")
                    .IsRequired(false);
            }
            catch
            {
                // Ignore mapping failures (e.g., if RagDocument type is different at runtime)
            }
        }

        // DbSet properties are non-nullable and assigned in the constructor via Set<T>().
        public DbSet<GuestMessage> GuestMessages { get; set; }
        public DbSet<GuestResponse> GuestResponses { get; set; }
        public DbSet<Config> Configs { get; set; }
        public DbSet<Rule> Rules { get; set; }
        public DbSet<RuleCategory> RuleCategories { get; set; }
        public DbSet<GuestAssessment> GuestAssessments { get; set; }
        public DbSet<GuestPayment> GuestPayments { get; set; }
        public DbSet<RagDocument> RagDocuments { get; set; }
        public DbSet<RagChunk> RagChunks { get; set; }
    }
}