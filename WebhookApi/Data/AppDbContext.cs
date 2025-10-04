// ... existing code ...

using Microsoft.EntityFrameworkCore;
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
        }
        
        public DbSet<GuestMessage> GuestMessages { get; set; }
        public DbSet<GuestResponse> GuestResponses { get; set; }
        public DbSet<Config> Configs { get; set; }
    }
}