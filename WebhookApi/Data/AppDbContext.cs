// ... existing code ...

using Microsoft.EntityFrameworkCore;

namespace WebhookApi.Data
{
    public class AppDbContext : DbContext
    {
        private readonly IConfiguration _configuration;

        public AppDbContext(DbContextOptions<AppDbContext> options, IConfiguration configuration)
        : base(options)
        {
            _configuration = configuration;
        }
        
        public DbSet<GuestMessage> GuestMessages { get; set; }
    }

    public class GuestMessage
    {
        public int Id { get; set; }
        public required string Message { get; set; }
        public string? Language { get; set; }
        public string? Category { get; set; }
        public string? Sentiment { get; set; }
        public string? ReplySuggestion { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}